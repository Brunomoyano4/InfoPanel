using System.Collections.Concurrent;
using System.Diagnostics;
using InfoPanel.Native;
using InfoPanel.Platform;

namespace InfoPanel.Controls;

/// <summary>
/// Aplicaciones instaladas (la carpeta "shell:AppsFolder" de Windows: escritorio y Store) y su lanzamiento.
/// Solo se pueden abrir apps de esa lista, nunca rutas o comandos arbitrarios.
/// </summary>
public sealed class AppLauncher : IAppLauncher
{
    private static readonly string[] Noise = { "uninstall", "desinstal", "readme", "help", "ayuda", "website", "sitio web", "documentation", "release notes" };

    private readonly ConcurrentDictionary<string, byte[]?> _icons = new();
    private List<AppDto>? _apps;
    private DateTime _appsRead;

    public List<AppDto> GetApps(bool refresh = false)
    {
        if (_apps is null || refresh || (DateTime.UtcNow - _appsRead).TotalMinutes > 10)
        {
            _apps = RunSta(ReadApps) ?? _apps ?? new();
            _appsRead = DateTime.UtcNow;
        }
        return _apps;
    }

    private static List<AppDto> ReadApps()
    {
        var list = new List<AppDto>();
        var shellType = Type.GetTypeFromProgID("Shell.Application")!;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic folder = shell.NameSpace("shell:AppsFolder");
        foreach (dynamic item in folder.Items())
        {
            string name = item.Name;
            string id = item.Path;
            if (string.IsNullOrWhiteSpace(id) || id.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
            if (Noise.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(new AppDto(id, name));
        }
        return list.DistinctBy(a => a.Id).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public byte[]? GetIcon(string id)
    {
        if (!GetApps().Any(a => a.Id == id)) return null;
        return _icons.GetOrAdd(id, key => RunSta(() => ShellImage.GetPng(@"shell:AppsFolder\" + key, 96)));
    }

    /// <summary>
    /// Abre la app a través del Explorador: así corre con los permisos normales del usuario
    /// y no con los de administrador de InfoPanel.
    /// </summary>
    public bool Launch(string id)
    {
        if (!GetApps().Any(a => a.Id == id)) return false;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"shell:AppsFolder\\{id}\"") { UseShellExecute = false })?.Dispose();
        return true;
    }

    // Las APIs del shell requieren un hilo STA
    private static T? RunSta<T>(Func<T> work)
    {
        T? result = default;
        var t = new Thread(() =>
        {
            try { result = work(); }
            catch { }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result;
    }
}
