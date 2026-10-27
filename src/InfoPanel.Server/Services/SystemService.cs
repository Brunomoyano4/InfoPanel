using System.Diagnostics;
using System.Management;
using Microsoft.Win32;

namespace InfoPanel.Services;

public record RamModuleDto(string Slot, string Manufacturer, string Part, long SizeGb, int SpeedMts);
public record GpuInfoDto(string Name, string Driver);
public record SystemInfoDto(string Windows, string Bios, string Board, List<RamModuleDto> Ram, List<GpuInfoDto> Gpus);

public record ProcessDto(int Pid, string Name, double Cpu, long MemoryMb);
public record DiskDto(string Name, string Label, string Format, long TotalGb, long FreeGb);
public record SystemDto(long UptimeSec, List<ProcessDto> Processes, List<DiskDto> Disks);

/// <summary>Datos de Windows que no cubre LibreHardwareMonitor: procesos, espacio en disco, uptime.</summary>
public sealed class SystemService
{
    private readonly Dictionary<int, TimeSpan> _lastCpu = new();
    private DateTime _lastSample = DateTime.MinValue;
    private List<ProcessDto> _lastTop = new();
    private List<DiskDto> _lastDisks = new();
    private DateTime _lastDiskSample = DateTime.MinValue;

    public SystemDto Sample()
    {
        var now = DateTime.UtcNow;
        // Los procesos se recalculan cada 2 s porque enumerar todos es costoso
        if ((now - _lastSample).TotalSeconds >= 2)
        {
            _lastTop = SampleProcesses(now);
            _lastSample = now;
        }
        if ((now - _lastDiskSample).TotalSeconds >= 30)
        {
            _lastDisks = SampleDisks();
            _lastDiskSample = now;
        }
        return new SystemDto(Environment.TickCount64 / 1000, _lastTop, _lastDisks);
    }

    private List<ProcessDto> SampleProcesses(DateTime now)
    {
        var elapsed = _lastSample == DateTime.MinValue ? 0 : (now - _lastSample).TotalMilliseconds;
        var cores = Environment.ProcessorCount;
        var seen = new HashSet<int>();
        var list = new List<ProcessDto>();

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == 0) continue;
                    var cpu = p.TotalProcessorTime;
                    seen.Add(p.Id);
                    double pct = 0;
                    if (elapsed > 0 && _lastCpu.TryGetValue(p.Id, out var prev))
                        pct = (cpu - prev).TotalMilliseconds / (elapsed * cores) * 100;
                    _lastCpu[p.Id] = cpu;
                    list.Add(new ProcessDto(p.Id, p.ProcessName, Math.Round(pct, 1), p.WorkingSet64 / 1024 / 1024));
                }
                catch
                {
                    // Procesos protegidos o que terminaron durante la lectura
                }
            }
        }

        foreach (var pid in _lastCpu.Keys.Where(k => !seen.Contains(k)).ToList())
            _lastCpu.Remove(pid);

        // Se envían todos para que el panel pueda ordenar por cualquier columna
        return list.OrderByDescending(p => p.Cpu).ThenByDescending(p => p.MemoryMb).ToList();
    }

    /// <summary>Datos fijos del equipo (se leen una vez): Windows, BIOS, placa, RAM y GPUs.</summary>
    public static SystemInfoDto ReadInfo()
    {
        static IEnumerable<ManagementObject> Wmi(string query)
        {
            try
            {
                using var s = new ManagementObjectSearcher(query);
                return s.Get().Cast<ManagementObject>().ToList();
            }
            catch
            {
                return Enumerable.Empty<ManagementObject>();
            }
        }
        static string Str(ManagementObject o, string prop) => o[prop]?.ToString()?.Trim() ?? "";

        var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var build = key?.GetValue("CurrentBuildNumber")?.ToString();
        // ProductName sigue diciendo "Windows 10" en Windows 11: se corrige por número de build
        var product = key?.GetValue("ProductName")?.ToString() ?? "Windows";
        if (int.TryParse(build, out var b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
        var windows = $"{product} {key?.GetValue("DisplayVersion")} (build {build}.{key?.GetValue("UBR")})";

        var bios = Wmi("SELECT Manufacturer, SMBIOSBIOSVersion FROM Win32_BIOS").Select(o => $"{Str(o, "Manufacturer")} {Str(o, "SMBIOSBIOSVersion")}").FirstOrDefault() ?? "";
        var board = Wmi("SELECT Manufacturer, Product FROM Win32_BaseBoard").Select(o => $"{Str(o, "Manufacturer")} {Str(o, "Product")}").FirstOrDefault() ?? "";
        var ram = Wmi("SELECT DeviceLocator, Manufacturer, PartNumber, Capacity, ConfiguredClockSpeed FROM Win32_PhysicalMemory")
            .Select(o => new RamModuleDto(Str(o, "DeviceLocator"), Str(o, "Manufacturer"), Str(o, "PartNumber"),
                Convert.ToInt64(o["Capacity"] ?? 0L) >> 30, Convert.ToInt32(o["ConfiguredClockSpeed"] ?? 0)))
            .ToList();
        var gpus = Wmi("SELECT Name, DriverVersion FROM Win32_VideoController")
            .Select(o => new GpuInfoDto(Str(o, "Name"), Str(o, "DriverVersion")))
            .ToList();

        return new SystemInfoDto(windows, bios, board, ram, gpus);
    }

    private static List<DiskDto> SampleDisks() =>
        DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
            .Select(d => new DiskDto(d.Name, d.VolumeLabel, d.DriveFormat, d.TotalSize >> 30, d.AvailableFreeSpace >> 30))
            .ToList();
}
