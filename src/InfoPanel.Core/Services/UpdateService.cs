using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using InfoPanel.Platform;

namespace InfoPanel.Services;

/// <summary>Estado de las actualizaciones que ve la app y la bandeja.</summary>
public record UpdateStateDto(string Current, string? Latest, bool Available, string? Notes, string? Url,
    string Status, string? Error, int Progress, bool CanInstall, DateTime? CheckedAt, bool CheckFailed);

/// <summary>
/// Actualizaciones desde los releases del repositorio público (un ejecutable por sistema). Revisa al arrancar y cada 6 horas. Instalar descarga el ejecutable nuevo,
/// verifica su SHA-256 contra el que informa GitHub, lo pone en lugar del actual (el que corre se renombra
/// a .old, Windows y Linux lo permiten) y arranca el nuevo según el sistema (IUpdatePlatform).
/// Configuración, certificados y dashboards están junto al ejecutable y no se tocan.
/// </summary>
public sealed class UpdateService : BackgroundService
{
    public const string Repo = "Brunomoyano4/InfoPanel";
    public const string AfterUpdateArg = "--after-update";

    // Para probar con un servidor local: INFOPANEL_UPDATE_URL apunta a un JSON con el formato de la API de GitHub
    private static readonly string ApiUrl = Environment.GetEnvironmentVariable("INFOPANEL_UPDATE_URL")
        ?? $"https://api.github.com/repos/{Repo}/releases/latest";

    public static readonly Version CurrentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
    public static string CurrentText => $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";

    // Solo el ejecutable único publicado puede reemplazarse a sí mismo; en desarrollo hay una .dll al lado
    public static bool CanInstall => !File.Exists(Path.Combine(AppContext.BaseDirectory, "InfoPanel.dll"));

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly ILogger<UpdateService> _log;
    private readonly IUpdatePlatform _platform;
    private readonly object _lock = new();
    private Release? _release;
    private string _status = "idle"; // idle | checking | downloading | installing | error
    private string? _error;
    private int _progress;
    private DateTime? _checkedAt;
    private bool _checkFailed; // la última búsqueda no pudo consultar GitHub

    /// <summary>Se dispara cuando el .exe nuevo ya está en su lugar y arrancó: este proceso tiene que salir.</summary>
    public event Action? RestartRequested;
    /// <summary>Se dispara la primera vez que se encuentra una versión nueva (para avisar en la bandeja).</summary>
    public event Action<string>? UpdateFound;

    public UpdateService(IUpdatePlatform platform, ILogger<UpdateService> log)
    {
        _platform = platform;
        _log = log;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"InfoPanel/{CurrentText}");
        CleanupOld();
    }

    public UpdateStateDto State
    {
        get
        {
            lock (_lock)
            {
                var latest = _release?.Version;
                return new UpdateStateDto(CurrentText, latest is null ? null : $"{latest.Major}.{latest.Minor}.{latest.Build}",
                    latest is not null && latest > CurrentVersion, _release?.Body, _release?.HtmlUrl,
                    _status, _error, _progress, CanInstall, _checkedAt, _checkFailed);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // La primera revisión espera un poco para no competir con el arranque (y con el Wi-Fi recién conectado)
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch { return; }
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        do { await CheckAsync(); } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    public async Task<string?> CheckAsync()
    {
        // Un error de instalación queda visible hasta el próximo intento, aunque se vuelva a buscar
        string before;
        lock (_lock)
        {
            if (_status is "downloading" or "installing") return null;
            before = _status;
            _status = "checking";
        }
        var after = before == "error" ? "error" : "idle";
        try
        {
            var release = await _http.GetFromJsonAsync<Release>(ApiUrl);
            var wasAvailable = State.Available;
            lock (_lock)
            {
                _release = release;
                _status = after;
                _checkedAt = DateTime.UtcNow;
                _checkFailed = false;
            }
            if (State.Available && !wasAvailable) UpdateFound?.Invoke(State.Latest!);
            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "No se pudo buscar actualizaciones");
            lock (_lock) { _status = after; _checkedAt = DateTime.UtcNow; _checkFailed = true; }
            return "No se pudo consultar las actualizaciones (¿sin internet?)";
        }
    }

    /// <summary>Descarga e instala la versión nueva en segundo plano. Devuelve un error si no se puede empezar.</summary>
    public string? StartInstall()
    {
        if (!CanInstall) return "La actualización automática solo funciona con el ejecutable publicado";
        Release release;
        Asset asset;
        lock (_lock)
        {
            if (_status is "downloading" or "installing") return null;
            if (_release is null || !(_release.Version > CurrentVersion)) return "No hay una versión nueva";
            release = _release;
            asset = release.Assets.FirstOrDefault(a => a.Name.Equals(_platform.AssetName, StringComparison.OrdinalIgnoreCase))!;
            if (asset is null) return $"El release no tiene {_platform.AssetName}";
            if (asset.Digest is not { } d || !d.StartsWith("sha256:")) return "El release no informa la huella del archivo; no se puede verificar";
            (_status, _error, _progress) = ("downloading", null, 0);
        }
        _ = Task.Run(() => InstallAsync(asset));
        return null;
    }

    private async Task InstallAsync(Asset asset)
    {
        var exe = Environment.ProcessPath!;
        var dir = Path.GetDirectoryName(exe)!;
        var (newPath, oldPath) = (Sibling(exe, ".new"), Sibling(exe, ".old"));
        try
        {
            // Descarga con progreso
            using (var resp = await _http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? asset.Size;
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(newPath);
                var buffer = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buffer)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n));
                    done += n;
                    if (total > 0) lock (_lock) _progress = (int)(done * 100 / total);
                }
            }

            // Verificación: el archivo tiene que ser exactamente el publicado
            lock (_lock) _status = "installing";
            string hash;
            await using (var f = File.OpenRead(newPath))
                hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(f));
            if (!asset.Digest!.Equals("sha256:" + hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("El archivo descargado no coincide con el publicado");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(newPath, (UnixFileMode)0b111_101_101); // 755: ejecutable

            // Reemplazo: el ejecutable en uso se puede renombrar; el nuevo toma su nombre
            if (File.Exists(oldPath)) File.Delete(oldPath);
            File.Move(exe, oldPath);
            try { File.Move(newPath, exe); }
            catch { File.Move(oldPath, exe); throw; }

            _platform.Relaunch(exe);
            RestartRequested?.Invoke();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "No se pudo instalar la actualización");
            try { if (File.Exists(newPath)) File.Delete(newPath); } catch { }
            lock (_lock) (_status, _error) = ("error", $"No se pudo actualizar: {ex.Message}");
        }
    }

    // InfoPanel.exe → InfoPanel.old.exe; InfoPanel (Linux) → InfoPanel.old
    private static string Sibling(string exe, string suffix) =>
        Path.Combine(Path.GetDirectoryName(exe)!, Path.GetFileNameWithoutExtension(exe) + suffix + Path.GetExtension(exe));

    // El ejecutable anterior queda renombrado hasta el próximo arranque (en Windows, mientras corría no se podía borrar)
    private static void CleanupOld()
    {
        if (Environment.ProcessPath is not { } exe) return;
        var old = Sibling(exe, ".old");
        for (int i = 0; i < 10 && File.Exists(old); i++)
        {
            try { File.Delete(old); }
            catch { Thread.Sleep(500); }
        }
    }

    private sealed class Release
    {
        [JsonPropertyName("tag_name")] public string Tag { get; set; } = "";
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("assets")] public List<Asset> Assets { get; set; } = new();
        public Version? Version => System.Version.TryParse(Tag.TrimStart('v', 'V'), out var v) ? v : null;
    }

    private sealed class Asset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("digest")] public string? Digest { get; set; }
        [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; } = "";
    }
}
