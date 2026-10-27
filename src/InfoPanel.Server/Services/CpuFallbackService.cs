using System.Diagnostics;
using System.Management;
using InfoPanel.Controls;

namespace InfoPanel.Services;

/// <summary>
/// Datos del CPU que no requieren el driver PawnIO de LibreHardwareMonitor:
/// temperatura por ACPI de ASUS o por las zonas térmicas ACPI de Windows (cualquier marca), consumo del paquete por los contadores RAPL de Windows
/// y clock efectivo por el contador de rendimiento del procesador.
/// Se agregan como sensores extra al CPU, y el panel los usa cuando los de LHM dan 0.
/// </summary>
public sealed class CpuFallbackService : IDisposable
{
    private const uint AsusCpuTemp = 0x00120094;

    private readonly AsusAcpi _acpi;
    private readonly PerformanceCounter? _rapl;
    private readonly PerformanceCounter? _perf;
    private readonly List<PerformanceCounter> _zones = new();
    private readonly float _baseMhz;

    public CpuFallbackService(AsusAcpi acpi)
    {
        _acpi = acpi;
        _rapl = TryCounter("Energy Meter", "Power", "RAPL_Package0_PKG");
        _perf = TryCounter("Processor Information", "% Processor Performance", "_Total");
        // Zonas térmicas ACPI: en décimas de kelvin. Suelen ser la del CPU y alguna del chipset
        try
        {
            foreach (var zone in new PerformanceCounterCategory("Thermal Zone Information").GetInstanceNames())
                if (TryCounter("Thermal Zone Information", "High Precision Temperature", zone) is { } c) _zones.Add(c);
        }
        catch { }
        try
        {
            using var s = new ManagementObjectSearcher("SELECT MaxClockSpeed FROM Win32_Processor");
            foreach (ManagementObject o in s.Get())
                using (o) _baseMhz = Convert.ToSingle(o["MaxClockSpeed"]);
        }
        catch { }
    }

    private static PerformanceCounter? TryCounter(string category, string counter, string instance)
    {
        try
        {
            var c = new PerformanceCounter(category, counter, instance, readOnly: true);
            c.NextValue(); // el primer valor siempre es 0
            return c;
        }
        catch
        {
            return null;
        }
    }

    public void Augment(List<HardwareDto> hardware)
    {
        var index = hardware.FindIndex(h => h.Type == "Cpu");
        if (index < 0) return;
        var cpu = hardware[index];
        var extra = new List<SensorDto>();

        if (_acpi.IsAvailable && _acpi.DeviceGet(AsusCpuTemp) is var t and > 0 and < 120)
            extra.Add(new SensorDto("/fallback/cpu/temperature", "CPU (ACPI ASUS)", "Temperature", t, null, null));
        else if (ZoneTemp() is > 0 and < 120 and var z)
            extra.Add(new SensorDto("/fallback/cpu/thermal-zone", "Zona térmica (ACPI)", "Temperature", z, null, null));

        if (_rapl is not null)
        {
            // El contador entrega milivatios
            var mw = Read(_rapl);
            if (mw > 0) extra.Add(new SensorDto("/fallback/cpu/power", "Package (RAPL)", "Power", MathF.Round(mw / 1000f, 2), null, null));
        }

        if (_perf is not null && _baseMhz > 0)
        {
            var pct = Read(_perf);
            if (pct > 0) extra.Add(new SensorDto("/fallback/cpu/clock", "Efectivo (Windows)", "Clock", MathF.Round(_baseMhz * pct / 100f), null, null));
        }

        if (extra.Count > 0)
            hardware[index] = cpu with { Sensors = cpu.Sensors.Concat(extra).ToList() };
    }

    // La zona más caliente es casi siempre la del CPU
    private float ZoneTemp()
    {
        var max = 0f;
        foreach (var z in _zones)
        {
            var c = Read(z) / 10f - 273.15f;
            if (c > max) max = c;
        }
        return MathF.Round(max, 1);
    }

    private static float Read(PerformanceCounter c)
    {
        try { return c.NextValue(); }
        catch { return 0; }
    }

    public void Dispose()
    {
        _rapl?.Dispose();
        _perf?.Dispose();
        _zones.ForEach(z => z.Dispose());
    }
}
