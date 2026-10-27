using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using InfoPanel.Controls;
using InfoPanel.Platform;

namespace InfoPanel;

/// <summary>
/// Controles en Linux con las herramientas del sistema:
/// - Audio con pactl (PulseAudio o PipeWire) o wpctl, y multimedia con playerctl. Viven en la sesión del usuario:
///   como el servicio corre como root, se ejecutan como el usuario que tiene la sesión gráfica abierta.
/// - Brillo por /sys/class/backlight. Energía con systemctl y loginctl.
/// - Modo de energía con power-profiles-daemon; si no está, con la preferencia de energía del CPU (EPP) o el regulador.
/// En un servidor sin escritorio no hay audio ni multimedia: esos controles quedan vacíos y la web los oculta.
/// </summary>
public sealed partial class LinuxSystemControl : ISystemControl
{
    private (DateTime At, (string User, string Runtime)? Session) _session = (DateTime.MinValue, null);

    public SystemControlStateDto GetState()
    {
        var (vol, muted) = GetVolume();
        return new SystemControlStateDto(vol, muted, GetBrightness(), GetPowerMode(), new(), GetMicMuted(), GetOutputs());
    }

    // ---------- Sesión del usuario ----------

    /// <summary>Usuario con sesión abierta (tiene audio o D-Bus en /run/user/UID). Se revisa cada 30 s.</summary>
    private (string User, string Runtime)? UserSession()
    {
        if ((DateTime.UtcNow - _session.At).TotalSeconds < 30) return _session.Session;
        (string, string)? found = null;
        var users = (Sys.Read("/etc/passwd") ?? "").Split('\n').Select(l => l.Split(':')).Where(p => p.Length > 2)
            .GroupBy(p => p[2]).ToDictionary(g => g.Key, g => g.First()[0]);
        foreach (var dir in Sys.Dirs("/run/user"))
        {
            var uid = Path.GetFileName(dir);
            if (uid == "0" || !users.TryGetValue(uid, out var user)) continue;
            if (new[] { "pulse/native", "pipewire-0", "bus" }.Any(f => File.Exists(Path.Combine(dir, f)) || Directory.Exists(Path.Combine(dir, f))))
            {
                found = (user, dir);
                break;
            }
        }
        _session = (DateTime.UtcNow, found);
        return found;
    }

    /// <summary>Ejecuta un comando en la sesión del usuario (como root, con runuser; si no, directamente).</summary>
    private (int Code, string Output) RunUser(string command, params string[] args)
    {
        if (Sys.Which(command) is not { } path) return (-1, "");
        if (!Sys.IsRoot) return Sys.Run(path, args);
        if (UserSession() is not { } s || Sys.Which("runuser") is not { } runuser) return (-1, "");
        return Sys.Run(runuser, new[] { "-u", s.User, "--", "env", $"XDG_RUNTIME_DIR={s.Runtime}",
            $"DBUS_SESSION_BUS_ADDRESS=unix:path={s.Runtime}/bus", path }.Concat(args), 3000);
    }

    private static bool Has(string command) => Sys.Which(command) is not null;

    // ---------- Audio ----------

    private (int?, bool?) GetVolume()
    {
        if (Has("pactl"))
        {
            var (code, output) = RunUser("pactl", "get-sink-volume", "@DEFAULT_SINK@");
            if (code != 0 || Percent().Match(output) is not { Success: true } m) return (null, null);
            return (int.Parse(m.Groups[1].Value), RunUser("pactl", "get-sink-mute", "@DEFAULT_SINK@").Output.Contains("yes"));
        }
        if (Has("wpctl"))
        {
            // "Volume: 0.45 [MUTED]"
            var (code, output) = RunUser("wpctl", "get-volume", "@DEFAULT_AUDIO_SINK@");
            if (code != 0 || Sys.ParseFloat(Regex.Match(output, @"Volume:\s*([\d.]+)").Groups[1].Value) is not { } v) return (null, null);
            return ((int)Math.Round(v * 100), output.Contains("MUTED"));
        }
        return (null, null);
    }

    public bool SetVolume(int? volume, bool? mute)
    {
        var ok = true;
        if (Has("pactl"))
        {
            if (volume is { } v) ok &= RunUser("pactl", "set-sink-volume", "@DEFAULT_SINK@", $"{Math.Clamp(v, 0, 100)}%").Code == 0;
            if (mute is { } m) ok &= RunUser("pactl", "set-sink-mute", "@DEFAULT_SINK@", m ? "1" : "0").Code == 0;
            return ok;
        }
        if (Has("wpctl"))
        {
            if (volume is { } v) ok &= RunUser("wpctl", "set-volume", "@DEFAULT_AUDIO_SINK@", (Math.Clamp(v, 0, 100) / 100.0).ToString("0.00", CultureInfo.InvariantCulture)).Code == 0;
            if (mute is { } m) ok &= RunUser("wpctl", "set-mute", "@DEFAULT_AUDIO_SINK@", m ? "1" : "0").Code == 0;
            return ok;
        }
        return false;
    }

    private bool? GetMicMuted()
    {
        if (Has("pactl"))
        {
            var (code, output) = RunUser("pactl", "get-source-mute", "@DEFAULT_SOURCE@");
            // Sin micrófono, la fuente predeterminada es el "monitor" de la salida: no cuenta
            if (code != 0 || RunUser("pactl", "get-default-source").Output.Trim().EndsWith(".monitor")) return null;
            return output.Contains("yes");
        }
        if (Has("wpctl"))
        {
            var (code, output) = RunUser("wpctl", "get-volume", "@DEFAULT_AUDIO_SOURCE@");
            return code == 0 ? output.Contains("MUTED") : null;
        }
        return null;
    }

    public bool SetMicMuted(bool mute)
    {
        if (Has("pactl"))
        {
            // Silencia todos los micrófonos, no solo el predeterminado
            var sources = RunUser("pactl", "list", "short", "sources").Output.Split('\n')
                .Select(l => l.Split('\t')).Where(p => p.Length > 1 && !p[1].EndsWith(".monitor")).Select(p => p[1]).ToList();
            return sources.Count > 0 && sources.All(s => RunUser("pactl", "set-source-mute", s, mute ? "1" : "0").Code == 0);
        }
        return Has("wpctl") && RunUser("wpctl", "set-mute", "@DEFAULT_AUDIO_SOURCE@", mute ? "1" : "0").Code == 0;
    }

    private List<AudioDeviceDto> GetOutputs()
    {
        if (!Has("pactl")) return new();
        var (code, output) = RunUser("pactl", "list", "sinks");
        if (code != 0) return new();
        var def = RunUser("pactl", "get-default-sink").Output.Trim();
        return SinkBlock().Matches(output)
            .Select(m => new AudioDeviceDto(m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim(), m.Groups[1].Value.Trim() == def))
            .ToList();
    }

    public bool SetOutput(string deviceId) =>
        GetOutputs().Any(d => d.Id == deviceId) && RunUser("pactl", "set-default-sink", deviceId).Code == 0;

    [GeneratedRegex(@"(\d+)%")]
    private static partial Regex Percent();

    // En "pactl list sinks" cada salida tiene "Name: ..." y, más abajo, "Description: ..."
    [GeneratedRegex(@"^\s*Name:\s*(.+)$[\s\S]*?^\s*Description:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex SinkBlock();

    // ---------- Brillo ----------

    private static string? Backlight() => Sys.Dirs("/sys/class/backlight").FirstOrDefault();

    private static int? GetBrightness()
    {
        if (Backlight() is not { } b || Sys.ReadLong(Path.Combine(b, "max_brightness")) is not { } max || max <= 0) return null;
        return Sys.ReadLong(Path.Combine(b, "brightness")) is { } v ? (int)Math.Round(v * 100.0 / max) : null;
    }

    public bool SetBrightness(int value)
    {
        if (Backlight() is not { } b || Sys.ReadLong(Path.Combine(b, "max_brightness")) is not { } max || max <= 0) return false;
        var raw = (long)Math.Round(Math.Clamp(value, 0, 100) / 100.0 * max);
        return Sys.Write(Path.Combine(b, "brightness"), raw.ToString(CultureInfo.InvariantCulture));
    }

    // ---------- Multimedia ----------

    public bool Media(string action)
    {
        var cmd = action switch { "playpause" => "play-pause", "next" => "next", "prev" => "previous", "stop" => "stop", _ => null };
        return cmd is not null && RunUser("playerctl", cmd).Code == 0;
    }

    // ---------- Energía ----------

    public bool Power(string action)
    {
        // Se lanzan sin esperar: apagar o reiniciar cortaría la respuesta
        return action switch
        {
            "lock" => Sys.Which("loginctl") is { } l && Sys.Start(l, "lock-sessions"),
            "sleep" => Systemctl("suspend"),
            "hibernate" => Systemctl("hibernate"),
            "restart" => Systemctl("reboot"),
            "shutdown" => Systemctl("poweroff"),
            _ => false,
        };
    }

    private static bool Systemctl(string verb) => Sys.Which("systemctl") is { } s && Sys.Start(s, verb);

    private const string CpuFreq = "/sys/devices/system/cpu/cpu0/cpufreq";

    private static IEnumerable<string> CpuFreqDirs() => Sys.Dirs("/sys/devices/system/cpu", "cpu*")
        .Where(d => Regex.IsMatch(Path.GetFileName(d), @"^cpu\d+$")).Select(d => Path.Combine(d, "cpufreq")).Where(Directory.Exists);

    private static string GetPowerMode()
    {
        if (Sys.Which("powerprofilesctl") is { } ppd && Sys.Run(ppd, "get") is (0, var profile))
            return profile.Trim() switch { "power-saver" => "efficiency", "balanced" => "balanced", "performance" => "performance", _ => "unknown" };
        if (Sys.Read(Path.Combine(CpuFreq, "energy_performance_preference")) is { } epp)
            return epp switch
            {
                "power" or "balance_power" => "efficiency",
                "performance" => "performance",
                _ => "balanced",
            };
        return Sys.Read(Path.Combine(CpuFreq, "scaling_governor")) switch
        {
            "powersave" => "efficiency",
            "performance" => "performance",
            "schedutil" or "ondemand" or "conservative" => "balanced",
            _ => "unknown",
        };
    }

    public bool SetPowerMode(string mode)
    {
        if (mode is not ("efficiency" or "balanced" or "performance")) return false;
        if (Sys.Which("powerprofilesctl") is { } ppd && Sys.Run(ppd, "get").Code == 0)
            return Sys.Run(ppd, "set", mode switch { "efficiency" => "power-saver", "performance" => "performance", _ => "balanced" }).Code == 0;

        var dirs = CpuFreqDirs().ToList();
        if (dirs.Count == 0) return false;
        if (File.Exists(Path.Combine(CpuFreq, "energy_performance_preference")))
        {
            // Con el regulador "performance" la preferencia queda fija: se vuelve al dinámico antes de cambiarla
            var epp = mode switch { "efficiency" => "power", "performance" => "performance", _ => "balance_performance" };
            return dirs.All(d =>
            {
                if (mode != "performance" && Sys.Read(Path.Combine(d, "scaling_governor")) == "performance")
                    Sys.Write(Path.Combine(d, "scaling_governor"), "powersave");
                return Sys.Write(Path.Combine(d, "energy_performance_preference"), epp);
            });
        }
        var available = (Sys.Read(Path.Combine(CpuFreq, "scaling_available_governors")) ?? "").Split(' ');
        var governor = mode switch
        {
            "efficiency" => "powersave",
            "performance" => "performance",
            _ => new[] { "schedutil", "ondemand", "conservative" }.FirstOrDefault(available.Contains),
        };
        return governor is not null && available.Contains(governor) && dirs.All(d => Sys.Write(Path.Combine(d, "scaling_governor"), governor));
    }

    // Linux no tiene planes de energía como Windows
    public bool SetPowerPlan(string id) => false;

    // ---------- Procesos ----------

    public bool KillProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
