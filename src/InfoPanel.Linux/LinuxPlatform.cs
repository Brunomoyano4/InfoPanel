using System.Diagnostics;
using InfoPanel.Platform;
using InfoPanel.Services;

namespace InfoPanel;

/// <summary>
/// Lo que ofrece este Linux y cómo se reinicia InfoPanel al actualizar.
/// Con systemd (el servicio de install.sh) alcanza con terminar: el servicio tiene Restart=always y arranca
/// el ejecutable nuevo. Corriendo a mano, se lanza el nuevo como en Windows.
/// </summary>
public sealed class LinuxPlatform : IPlatformInfo, IUpdatePlatform
{
    public const string ServiceName = "infopanel.service";

    public PlatformCapsDto Caps { get; } = new("linux", PowerPlans: false, Displays: false,
        Media: Sys.Which("playerctl") is not null, PowerActions(), AutoStartLabel: "el sistema");

    public string AssetName => "InfoPanel-linux-x64";

    public static bool UnderSystemd => Environment.GetEnvironmentVariable("INVOCATION_ID") is not null;

    public void Relaunch(string exePath)
    {
        if (UnderSystemd) return;
        Process.Start(new ProcessStartInfo(exePath, UpdateService.AfterUpdateArg)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
        })?.Dispose();
    }

    // Suspender e hibernar solo si el kernel los ofrece (/sys/power/state); bloquear si hay una sesión gráfica
    private static List<string> PowerActions()
    {
        var actions = new List<string>();
        var states = (Sys.Read("/sys/power/state") ?? "").Split(' ');
        if (HasGraphicalSession()) actions.Add("lock");
        if (states.Contains("mem")) actions.Add("sleep");
        if (states.Contains("disk")) actions.Add("hibernate");
        actions.Add("restart");
        actions.Add("shutdown");
        return actions;
    }

    private static bool HasGraphicalSession()
    {
        if (Sys.Which("loginctl") is not { } loginctl) return false;
        foreach (var line in Sys.Run(loginctl, "list-sessions", "--no-legend").Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var id = line.Trim().Split(' ')[0];
            if (Sys.Run(loginctl, "show-session", id, "-p", "Type", "--value").Output.Trim() is "x11" or "wayland") return true;
        }
        return false;
    }
}

/// <summary>Inicio con el sistema: habilitar o deshabilitar el servicio de systemd que crea install.sh.</summary>
public sealed class LinuxAutoStart : IAutoStart
{
    private (DateTime At, bool Enabled) _cache = (DateTime.MinValue, false);

    public bool IsEnabled
    {
        get
        {
            if ((DateTime.UtcNow - _cache.At).TotalSeconds > 10)
                _cache = (DateTime.UtcNow, Systemctl("is-enabled").Output.Trim() == "enabled");
            return _cache.Enabled;
        }
    }

    public string? SetEnabled(bool enabled)
    {
        if (Sys.Which("systemctl") is null) return "Este sistema no usa systemd";
        if (!File.Exists($"/etc/systemd/system/{LinuxPlatform.ServiceName}")) return "InfoPanel no está instalado como servicio (usá install.sh)";
        var (code, output) = Systemctl(enabled ? "enable" : "disable");
        _cache = (DateTime.MinValue, false);
        return code == 0 ? null : "systemctl falló: " + output.Trim();
    }

    private static (int Code, string Output) Systemctl(string verb) =>
        Sys.Which("systemctl") is { } s ? Sys.Run(s, verb, LinuxPlatform.ServiceName) : (-1, "");
}
