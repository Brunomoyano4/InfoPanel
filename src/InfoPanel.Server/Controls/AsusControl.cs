namespace InfoPanel.Controls;

public record AsusFanDto(string Fan, int Rpm);
public record FanCurveDto(string Fan, int[] Temps, int[] Speeds);
public record AsusStateDto(bool Available, int Mode, string ModeName, List<AsusFanDto> Fans);

/// <summary>Modos de rendimiento y curvas de ventilador de equipos ASUS (ROG Ally, laptops ROG/TUF).</summary>
public sealed class AsusControl
{
    public static readonly string[] ModeNames = { "Balanced", "Turbo", "Silent" };

    private static readonly (string Name, uint Speed, uint Curve)[] FanDevices =
    {
        ("cpu", AsusAcpi.CpuFanSpeed, AsusAcpi.CpuFanCurve),
        ("gpu", AsusAcpi.GpuFanSpeed, AsusAcpi.GpuFanCurve),
        ("mid", AsusAcpi.MidFanSpeed, AsusAcpi.MidFanCurve),
    };

    private readonly AsusAcpi _acpi;
    private int _mode;
    private DateTime _modeChecked = DateTime.MinValue;
    private string? _lastPlan;
    // El ACPI siempre devuelve la curva de fábrica: se recuerdan las aplicadas hasta el próximo cambio de modo
    private readonly Dictionary<(int Mode, string Fan), FanCurveDto> _applied = new();

    private readonly Services.AuthService _config;

    public AsusControl(AsusAcpi acpi, Services.AuthService config)
    {
        _acpi = acpi;
        _config = config;
        // El firmware puede volver a 100 % tras reiniciar: se reaplica el último límite elegido
        if (config.Config.BatteryLimit is { } limit && acpi.IsAvailable)
            acpi.DeviceSet(AsusAcpi.BatteryLimit, limit);
    }

    /// <summary>Último límite de carga aplicado desde InfoPanel (el ACPI no permite leerlo).</summary>
    public int? BatteryLimit => Available ? _config.Config.BatteryLimit : null;

    public string? SetBatteryLimit(int percent)
    {
        if (!Available) return "ACPI de ASUS no disponible";
        if (percent is < 40 or > 100) return "El límite debe estar entre 40 y 100 %";
        if (_acpi.DeviceSet(AsusAcpi.BatteryLimit, percent) != 1) return "El equipo rechazó el límite";
        _config.Config.BatteryLimit = percent;
        _config.SaveConfig();
        return null;
    }

    /// <summary>Se dispara al cambiar de modo; el firmware resetea curvas y límites de potencia.</summary>
    public event Action? ModeChanged;

    public bool Available => _acpi.IsAvailable;

    /// <summary>
    /// El ACPI no permite leer el modo actual, así que se deduce del plan de energía
    /// que activa Armoury Crate (Silent / Performance / Turbo). Solo se toma cuando el plan cambia,
    /// porque un cambio de modo hecho desde InfoPanel no modifica el plan.
    /// </summary>
    private void RefreshMode()
    {
        var now = DateTime.UtcNow;
        if ((now - _modeChecked).TotalSeconds < 5) return;
        _modeChecked = now;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("powercfg", "/getactivescheme")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            var name = output[(output.LastIndexOf('(') + 1)..].TrimEnd(')', ' ', '\r', '\n');
            if (name == _lastPlan) return;
            int? mode = name.ToLowerInvariant() switch
            {
                "silent" => 2,
                "turbo" => 1,
                "performance" or "balanced" => 0,
                _ => null,
            };
            if (mode is { } m && _lastPlan is not null)
            {
                _mode = m;
                _applied.Clear();
                ModeChanged?.Invoke();
            }
            else if (mode is { } first) _mode = first;
            _lastPlan = name;
        }
        catch { }
    }

    public AsusStateDto GetState()
    {
        if (!Available) return new AsusStateDto(false, 0, "", new());
        RefreshMode();
        var fans = new List<AsusFanDto>();
        foreach (var (name, speed, _) in FanDevices)
        {
            var v = _acpi.DeviceGet(speed);
            if (v >= 0) fans.Add(new AsusFanDto(name, v * 100));
        }
        return new AsusStateDto(true, _mode, ModeNames[_mode], fans);
    }

    /// <summary>Cambia el modo (0 Balanced, 1 Turbo, 2 Silent). Resetea las curvas a las de fábrica del modo.</summary>
    public bool SetMode(int mode)
    {
        if (!Available || mode is < 0 or > 2) return false;
        var ok = _acpi.DeviceSet(AsusAcpi.PerformanceMode, mode) == 1;
        if (ok)
        {
            _mode = mode;
            _applied.Clear();
            ModeChanged?.Invoke();
        }
        return ok;
    }

    public List<FanCurveDto> GetCurves(int? mode = null)
    {
        var result = new List<FanCurveDto>();
        if (!Available) return result;
        // En el ACPI de ASUS los índices de Turbo y Silent están invertidos para la lectura de curvas
        var m = mode ?? _mode;
        uint acpiMode = m switch { 1 => 2, 2 => 1, _ => 0 };
        foreach (var (name, _, curve) in FanDevices)
        {
            if (_applied.TryGetValue((m, name), out var applied))
            {
                result.Add(applied);
                continue;
            }
            var buf = _acpi.DeviceGetBuffer(curve, acpiMode);
            if (!IsValidCurve(buf)) continue;
            result.Add(new FanCurveDto(name, buf[..8].Select(b => (int)b).ToArray(), buf[8..16].Select(b => (int)b).ToArray()));
        }
        return result;
    }

    /// <summary>Aplica una curva de 8 puntos (temperatura °C → velocidad %) al modo actual.</summary>
    public string? SetCurve(FanCurveDto curve)
    {
        if (!Available) return "ACPI de ASUS no disponible";
        var device = FanDevices.FirstOrDefault(f => f.Name == curve.Fan);
        if (device.Name is null) return "Ventilador desconocido";
        if (curve.Temps.Length != 8 || curve.Speeds.Length != 8) return "La curva debe tener 8 puntos";

        var buf = new byte[16];
        for (int i = 0; i < 8; i++)
        {
            buf[i] = (byte)Math.Clamp(curve.Temps[i], 20, 110);
            buf[8 + i] = (byte)Math.Clamp(curve.Speeds[i], 0, 100);
        }
        if (!IsValidCurve(buf)) return "Las temperaturas y velocidades deben ser crecientes";

        if (_acpi.DeviceSet(device.Curve, buf) != 1) return "El equipo rechazó la curva";
        _applied[(_mode, curve.Fan)] = new FanCurveDto(curve.Fan, buf[..8].Select(b => (int)b).ToArray(), buf[8..16].Select(b => (int)b).ToArray());
        return null;
    }

    private static bool IsValidCurve(byte[] buf)
    {
        if (buf.Length < 16 || buf[0] == 0) return false;
        for (int i = 1; i < 8; i++)
            if (buf[i] < buf[i - 1] || buf[8 + i] < buf[8 + i - 1]) return false;
        return true;
    }
}
