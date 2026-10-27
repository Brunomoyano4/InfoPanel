using System.Text.RegularExpressions;
using InfoPanel.Platform;
using InfoPanel.Services;

namespace InfoPanel;

/// <summary>Procesos, espacio en disco, uptime, alimentación y datos fijos del equipo en Linux.</summary>
public sealed partial class LinuxSystemMonitor : ISystemMonitor
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
        var uptime = Sys.ParseFloat(Sys.Read("/proc/uptime")?.Split(' ')[0]) ?? Environment.TickCount64 / 1000f;
        var (onAc, charging) = PowerStatus();
        return new SystemDto((long)uptime, _lastTop, _lastDisks, onAc, charging);
    }

    // Enchufada si alguna fuente "Mains" (o USB-C de carga) está en línea; sin fuentes (PC de escritorio o servidor), sin dato
    private static (bool? OnAc, bool Charging) PowerStatus()
    {
        bool? onAc = null;
        var charging = false;
        foreach (var dir in Sys.Dirs("/sys/class/power_supply"))
        {
            var type = Sys.Read(Path.Combine(dir, "type"));
            if (type is "Mains" or "USB" or "USB_C" or "USB_PD" && Sys.Read(Path.Combine(dir, "online")) is { } online)
                onAc = (onAc ?? false) || online == "1";
            if (type == "Battery" && Sys.Read(Path.Combine(dir, "scope")) != "Device" && Sys.Read(Path.Combine(dir, "status")) == "Charging")
                charging = true;
        }
        return (onAc, charging);
    }

    // Sistemas de archivos reales (no los virtuales del kernel ni las capas de contenedores)
    private static readonly HashSet<string> RealFs = new() { "ext2", "ext3", "ext4", "xfs", "btrfs", "zfs", "vfat", "exfat", "ntfs", "ntfs3", "f2fs", "jfs", "reiserfs", "bcachefs" };

    /// <summary>
    /// Una entrada por dispositivo (la raíz primero), sin los montajes de solo lectura ni los de menos de 1 GB
    /// (herramientas o imágenes montadas por el sistema). En ZFS, cada dataset es un montaje: se muestran solo la raíz
    /// y los pools (sin "/" en el nombre), no los datasets de cada VM o contenedor de Proxmox.
    /// </summary>
    private static List<DiskDto> SampleDisks()
    {
        var disks = new List<DiskDto>();
        var seen = new HashSet<string>();
        var mounts = (Sys.Read("/proc/self/mounts") ?? "").Split('\n')
            .Select(l => l.Split(' '))
            .Where(p => p.Length >= 4 && RealFs.Contains(p[2]) && !p[3].Split(',').Contains("ro"))
            .Select(p => (Device: Unescape(p[0]), Mount: Unescape(p[1]), Fs: p[2]))
            .OrderBy(m => m.Mount == "/" ? 0 : 1).ThenBy(m => m.Mount.Length);
        foreach (var (device, mount, fs) in mounts)
        {
            if (fs == "zfs" ? mount != "/" && device.Contains('/') : !device.StartsWith("/dev/") || device.StartsWith("/dev/loop")) continue;
            if (!seen.Add(device)) continue;
            try
            {
                var d = new DriveInfo(mount);
                if (d.TotalSize < 1L << 30) continue;
                var label = fs == "zfs" ? device : Path.GetFileName(device);
                disks.Add(new DiskDto(mount, label, fs, d.TotalSize >> 30, d.AvailableFreeSpace >> 30));
            }
            catch { }
        }
        return disks;
    }

    // /proc/self/mounts escapa espacios y otros caracteres como \040
    private static string Unescape(string s) => OctalEscape().Replace(s, m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString());

    [GeneratedRegex(@"\\([0-7]{3})")]
    private static partial Regex OctalEscape();

    /// <summary>Datos fijos del equipo: distribución y kernel, BIOS, placa, RAM (dmidecode, como root) y GPUs (lspci).</summary>
    public SystemInfoDto ReadInfo()
    {
        var osRelease = Sys.Read("/etc/os-release") ?? "";
        var pretty = Regex.Match(osRelease, "^PRETTY_NAME=\"?([^\"\\n]+)\"?", RegexOptions.Multiline);
        var os = $"{(pretty.Success ? pretty.Groups[1].Value : "Linux")} (kernel {Sys.Read("/proc/sys/kernel/osrelease")})";

        string Dmi(string f) => Sys.Read(Path.Combine("/sys/class/dmi/id", f)) ?? "";
        var bios = $"{Dmi("bios_vendor")} {Dmi("bios_version")}".Trim();
        var board = $"{Dmi("board_vendor")} {Dmi("board_name")}".Trim();
        if (board.Length == 0) board = $"{Dmi("sys_vendor")} {Dmi("product_name")}".Trim();

        return new SystemInfoDto(os, bios, board, ReadRam(), ReadGpus());
    }

    private static List<RamModuleDto> ReadRam()
    {
        var list = new List<RamModuleDto>();
        if (Sys.Which("dmidecode") is not { } dmidecode) return list;
        foreach (var block in Sys.Run(dmidecode, "-t", "17").Output.Split("\n\n"))
        {
            string Field(string name) => Regex.Match(block, $@"^\s*{name}:\s*(.+)$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value.Trim() : "";
            var size = Regex.Match(Field("Size"), @"(\d+)\s*(GB|MB)");
            if (!size.Success) continue; // ranura vacía
            var gb = long.Parse(size.Groups[1].Value) / (size.Groups[2].Value == "MB" ? 1024 : 1);
            var speed = Regex.Match(Field("Configured Memory Speed") is { Length: > 0 } c ? c : Field("Speed"), @"\d+");
            list.Add(new RamModuleDto(Field("Locator"), Field("Manufacturer"), Field("Part Number"), gb, speed.Success ? int.Parse(speed.Value) : 0));
        }
        return list;
    }

    private static List<GpuInfoDto> ReadGpus()
    {
        var list = new List<GpuInfoDto>();
        if (Sys.Which("lspci") is not { } lspci) return list;
        foreach (var line in Sys.Run(lspci, "-mmD").Output.Split('\n'))
        {
            var q = Regex.Matches(line, "\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToList();
            if (q.Count < 3 || !Regex.IsMatch(q[0], "VGA|3D|Display")) continue;
            var slot = line.Split(' ')[0];
            var driver = Sys.RealPath($"/sys/bus/pci/devices/{slot}/driver") is { } d ? Path.GetFileName(d) : "";
            var version = driver == "nvidia" ? Sys.Read("/sys/module/nvidia/version") : Sys.Read($"/sys/module/{driver}/version");
            var bracket = Regex.Match(q[2], @"\[(.+)\]");
            list.Add(new GpuInfoDto($"{q[1].Replace(" Corporation", "")} {(bracket.Success ? bracket.Groups[1].Value : q[2])}",
                $"{driver} {version}".Trim()));
        }
        return list;
    }
}
