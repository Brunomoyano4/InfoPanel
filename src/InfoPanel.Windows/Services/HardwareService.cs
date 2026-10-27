using InfoPanel.Platform;
using LibreHardwareMonitor.Hardware;

namespace InfoPanel.Services;

/// <summary>
/// Lectura de sensores vía LibreHardwareMonitor (CPU, GPU, RAM, placa, discos, red, batería)
/// y control de ventiladores de placas compatibles. Suma los sensores alternativos del CPU (CpuFallbackService).
/// </summary>
public sealed class HardwareService : IHardwareMonitor
{
    private readonly Computer _computer;
    private readonly CpuFallbackService _cpuFallback;
    private readonly object _lock = new();

    public HardwareService(CpuFallbackService cpuFallback)
    {
        _cpuFallback = cpuFallback;
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsStorageEnabled = true,
            IsNetworkEnabled = true,
            IsBatteryEnabled = true,
            IsPsuEnabled = true,
        };
        _computer.Open();
    }

    public string CpuName
    {
        get
        {
            lock (_lock)
                return _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu)?.Name ?? "CPU";
        }
    }

    public List<HardwareDto> Update()
    {
        var result = new List<HardwareDto>();
        lock (_lock)
        {
            foreach (var hw in _computer.Hardware)
                Collect(hw, result);
        }
        _cpuFallback.Augment(result);
        return result;
    }

    // Adaptadores virtuales/filtros de Windows que solo ensucian la lista de red
    private static readonly System.Text.RegularExpressions.Regex NetworkNoise =
        new(@"Filter|Scheduler|LightWeight|Kernel Debugger|Local Area Connection\*|Bluetooth", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static void Collect(IHardware hw, List<HardwareDto> result)
    {
        if (hw.HardwareType == HardwareType.Network && NetworkNoise.IsMatch(hw.Name)) return;
        hw.Update();
        var sensors = hw.Sensors
            .Select(s => new SensorDto(s.Identifier.ToString(), s.Name, s.SensorType.ToString(), Clean(s.Value), Clean(s.Min), Clean(s.Max)))
            .ToList();
        if (sensors.Count > 0)
            result.Add(new HardwareDto(hw.Identifier.ToString(), hw.Name, hw.HardwareType.ToString(), sensors));

        foreach (var sub in hw.SubHardware)
            Collect(sub, result);
    }

    private static float? Clean(float? v) => v is null || float.IsNaN(v.Value) || float.IsInfinity(v.Value) ? null : MathF.Round(v.Value, 2);

    public List<FanControlDto> GetFanControls()
    {
        var list = new List<FanControlDto>();
        lock (_lock)
        {
            foreach (var sensor in AllSensors())
            {
                if (sensor.Control is not { } c) continue;
                list.Add(new FanControlDto(sensor.Identifier.ToString(), sensor.Name, sensor.Hardware.Name,
                    c.ControlMode.ToString(), Clean(c.SoftwareValue), c.MinSoftwareValue, c.MaxSoftwareValue));
            }
        }
        return list;
    }

    /// <summary>Fija un ventilador en porcentaje, o lo devuelve al control automático si percent es null.</summary>
    public bool SetFan(string id, float? percent)
    {
        lock (_lock)
        {
            var sensor = AllSensors().FirstOrDefault(s => s.Identifier.ToString() == id);
            if (sensor?.Control is not { } c) return false;
            if (percent is null)
                c.SetDefault();
            else
                c.SetSoftware(Math.Clamp(percent.Value, c.MinSoftwareValue, c.MaxSoftwareValue));
            return true;
        }
    }

    private IEnumerable<ISensor> AllSensors()
    {
        static IEnumerable<IHardware> Walk(IHardware h) => new[] { h }.Concat(h.SubHardware.SelectMany(Walk));
        return _computer.Hardware.SelectMany(Walk).SelectMany(h => h.Sensors);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            // Devuelve los ventiladores a automático al cerrar
            foreach (var s in AllSensors())
                s.Control?.SetDefault();
            _computer.Close();
        }
    }
}
