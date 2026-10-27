using System.Management;
using InfoPanel.Platform;
using Microsoft.Win32;

namespace InfoPanel.Services;

/// <summary>Datos de Windows que no cubre LibreHardwareMonitor: procesos, espacio en disco, uptime.</summary>
public sealed class SystemService : ISystemMonitor
{
    private readonly ProcessSampler _processes = new();
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
            _lastTop = _processes.Sample();
            _lastSample = now;
        }
        if ((now - _lastDiskSample).TotalSeconds >= 30)
        {
            _lastDisks = SampleDisks();
            _lastDiskSample = now;
        }
        var power = System.Windows.Forms.SystemInformation.PowerStatus;
        bool? onAc = power.PowerLineStatus switch
        {
            System.Windows.Forms.PowerLineStatus.Online => true,
            System.Windows.Forms.PowerLineStatus.Offline => false,
            _ => null,
        };
        var charging = power.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.Charging);
        return new SystemDto(Environment.TickCount64 / 1000, _lastTop, _lastDisks, onAc, charging);
    }

    /// <summary>Datos fijos del equipo (se leen una vez): Windows, BIOS, placa, RAM y GPUs.</summary>
    public SystemInfoDto ReadInfo()
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
