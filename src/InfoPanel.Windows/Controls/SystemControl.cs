using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;
using InfoPanel.Native;
using InfoPanel.Platform;
using NAudio.CoreAudioApi;

namespace InfoPanel.Controls;

/// <summary>Controles de Windows: volumen, brillo, multimedia, energía y procesos.</summary>
public sealed partial class SystemControl : ISystemControl
{
    // Modos de energía de Windows 11 (overlay)
    public static readonly Dictionary<string, Guid> PowerModes = new()
    {
        ["efficiency"] = new("961cc777-2547-4f9d-8174-7d86181b8a7a"),
        ["balanced"] = Guid.Empty,
        ["performance"] = new("ded574b5-45a0-4f42-8737-46345c09c238"),
    };

    private List<PowerPlanDto> _plans = new();
    private DateTime _plansRead = DateTime.MinValue;

    public SystemControlStateDto GetState()
    {
        if ((DateTime.UtcNow - _plansRead).TotalSeconds > 10)
        {
            _plans = ReadPowerPlans();
            _plansRead = DateTime.UtcNow;
        }
        var (vol, muted) = GetVolume();
        return new SystemControlStateDto(vol, muted, GetBrightness(), GetPowerMode(), _plans, GetMicMuted(), GetOutputs());
    }

    // ---------- Micrófono y dispositivos de salida ----------

    private static bool? GetMicMuted()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            using var dev = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            return dev.AudioEndpointVolume.Mute;
        }
        catch
        {
            return null; // sin micrófono
        }
    }

    public bool SetMicMuted(bool mute)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            // Silencia todos los micrófonos activos, no solo el predeterminado
            foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                using (dev) dev.AudioEndpointVolume.Mute = mute;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static List<AudioDeviceDto> GetOutputs()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            string? defaultId = null;
            try
            {
                using var def = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                defaultId = def.ID;
            }
            catch { }

            var list = new List<AudioDeviceDto>();
            foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                using (dev) list.Add(new AudioDeviceDto(dev.ID, dev.FriendlyName, dev.ID == defaultId));
            return list;
        }
        catch
        {
            return new();
        }
    }

    public bool SetOutput(string deviceId)
    {
        if (!GetOutputs().Any(d => d.Id == deviceId)) return false;
        try
        {
            return AudioPolicy.SetDefault(deviceId);
        }
        catch
        {
            return false;
        }
    }

    // ---------- Audio ----------

    private static (int?, bool?) GetVolume()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            using var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return ((int)Math.Round(dev.AudioEndpointVolume.MasterVolumeLevelScalar * 100), dev.AudioEndpointVolume.Mute);
        }
        catch
        {
            return (null, null);
        }
    }

    public bool SetVolume(int? volume, bool? mute)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            using var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (volume is { } v) dev.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(v, 0, 100) / 100f;
            if (mute is { } m) dev.AudioEndpointVolume.Mute = m;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------- Brillo (pantallas internas) ----------

    private static int? GetBrightness()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject o in s.Get())
                using (o) return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch { }
        return null;
    }

    public bool SetBrightness(int value)
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    o.InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)Math.Clamp(value, 0, 100) });
                    return true;
                }
        }
        catch { }
        return false;
    }

    // ---------- Multimedia ----------

    public bool Media(string action)
    {
        byte? key = action switch
        {
            "playpause" => Win32.VK_MEDIA_PLAY_PAUSE,
            "next" => Win32.VK_MEDIA_NEXT_TRACK,
            "prev" => Win32.VK_MEDIA_PREV_TRACK,
            "stop" => Win32.VK_MEDIA_STOP,
            _ => null,
        };
        if (key is null) return false;
        Win32.keybd_event(key.Value, 0, 0, UIntPtr.Zero);
        Win32.keybd_event(key.Value, 0, Win32.KEYEVENTF_KEYUP, UIntPtr.Zero);
        return true;
    }

    // ---------- Energía ----------

    public bool Power(string action)
    {
        switch (action)
        {
            case "lock": return Win32.LockWorkStation();
            case "sleep": return Win32.SetSuspendState(false, false, false);
            case "hibernate": return Win32.SetSuspendState(true, false, false);
            case "shutdown": Run("shutdown", "/s /t 0"); return true;
            case "restart": Run("shutdown", "/r /t 0"); return true;
            default: return false;
        }
    }

    private static string GetPowerMode()
    {
        if (Win32.PowerGetEffectiveOverlayScheme(out var g) != 0) return "unknown";
        return PowerModes.FirstOrDefault(kv => kv.Value == g).Key ?? "unknown";
    }

    public bool SetPowerMode(string mode) =>
        PowerModes.TryGetValue(mode, out var g) && Win32.PowerSetActiveOverlayScheme(g) == 0;

    private static List<PowerPlanDto> ReadPowerPlans()
    {
        var output = Run("powercfg", "/list");
        return PlanRegex().Matches(output)
            .Select(m => new PowerPlanDto(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Success))
            .ToList();
    }

    public bool SetPowerPlan(string guid)
    {
        if (!Guid.TryParse(guid, out var g)) return false;
        Run("powercfg", $"/setactive {g}");
        _plansRead = DateTime.MinValue;
        return true;
    }

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

    private static string Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(5000);
        return output;
    }

    // Ej.: "Power Scheme GUID: 381b4222-...  (Balanced) *"
    [GeneratedRegex(@"GUID[^:]*:\s*([0-9a-fA-F-]{36})\s+\((.+?)\)\s*(\*)?")]
    private static partial Regex PlanRegex();
}
