using System.Globalization;
using System.Text.RegularExpressions;
using InfoPanel.Platform;
using InfoPanel.Services;

namespace InfoPanel;

/// <summary>
/// Sensores en Linux, leídos de /sys y /proc (y nvidia-smi para placas NVIDIA), con los mismos tipos y nombres
/// que LibreHardwareMonitor en Windows ("CPU Total", "Memory Used", "GPU Core"...), así la web los usa igual.
/// - CPU: carga de /proc/stat, clocks de cpufreq, temperatura de coretemp/k10temp (o zonas térmicas) y consumo de RAPL.
/// - Memoria: /proc/meminfo. GPU: NVIDIA (nvidia-smi), AMD (amdgpu) e Intel (frecuencia y carga por residencia RC6).
/// - Discos: /proc/diskstats y su temperatura (nvme, drivetemp). Red: /proc/net/dev de las placas físicas.
/// - Placa: el resto de los chips hwmon (ventiladores, temperaturas, voltajes) y control de ventiladores por PWM.
/// - Batería: /sys/class/power_supply.
/// Leer el consumo (RAPL) y controlar ventiladores requiere root: el servicio corre como root.
/// </summary>
public sealed partial class LinuxHardwareMonitor : IHardwareMonitor
{
    private record Hwmon(string Dir, string Name, string? Device);
    private record FanControl(string Id, string Name, string Hardware, string PwmPath, string EnablePath);

    private readonly object _lock = new();
    private readonly Dictionary<string, (float Min, float Max)> _minMax = new();
    private readonly Dictionary<string, (double Value, DateTime At)> _prev = new();
    private readonly HashSet<string> _fansSeen = new();
    private readonly Dictionary<string, FanControl> _fanControls = new();
    private readonly Dictionary<string, string> _fanOriginalMode = new();
    private List<Hwmon> _hwmons = new();
    private DateTime _hwmonsRead = DateTime.MinValue;
    private Dictionary<string, string>? _pciNames;
    private (DateTime At, List<HardwareDto> Gpus) _nvidia = (DateTime.MinValue, new());

    public string CpuName { get; } = ReadCpuName();

    public List<HardwareDto> Update()
    {
        lock (_lock)
        {
            if ((DateTime.UtcNow - _hwmonsRead).TotalSeconds > 60)
            {
                _hwmons = ScanHwmons();
                _hwmonsRead = DateTime.UtcNow;
            }
            var used = new HashSet<Hwmon>();
            var result = new List<HardwareDto>();
            Add(result, Cpu(used));
            result.AddRange(Memory());
            result.AddRange(Gpus(used));
            result.AddRange(Storage(used));
            result.AddRange(Network());
            result.AddRange(Chips(used));
            result.AddRange(Batteries());
            return result;
        }
    }

    private static void Add(List<HardwareDto> list, HardwareDto? hw)
    {
        if (hw is not null) list.Add(hw);
    }

    // ---------- Utilidades ----------

    private SensorDto Sensor(string hwId, string name, string type, float? value, string? key = null)
    {
        var id = $"{hwId}/{type.ToLowerInvariant()}/{key ?? name}";
        float? v = value is null || float.IsNaN(value.Value) || float.IsInfinity(value.Value) ? null : MathF.Round(value.Value, 2);
        float? min = null, max = null;
        if (v is { } x)
        {
            var (mn, mx) = _minMax.TryGetValue(id, out var m) ? m : (x, x);
            _minMax[id] = (MathF.Min(mn, x), MathF.Max(mx, x));
            (min, max) = _minMax[id];
        }
        return new SensorDto(id, name, type, v, min, max);
    }

    /// <summary>Variación por segundo de un contador acumulado (bytes, microjoules...) desde la lectura anterior.</summary>
    private double? Rate(string key, double? total, double scale = 1)
    {
        if (total is not { } t) return null;
        var now = DateTime.UtcNow;
        double? rate = null;
        if (_prev.TryGetValue(key, out var p) && t >= p.Value)
        {
            var secs = (now - p.At).TotalSeconds;
            if (secs > 0.05) rate = (t - p.Value) / secs * scale;
        }
        _prev[key] = (t, now);
        return rate;
    }

    private static List<Hwmon> ScanHwmons() => Sys.Dirs("/sys/class/hwmon", "hwmon*")
        .Select(d => new Hwmon(d, Sys.Read(Path.Combine(d, "name")) ?? "", Sys.RealPath(Path.Combine(d, "device"))))
        .ToList();

    /// <summary>Archivos de un tipo de sensor de un chip hwmon: (número, ruta del valor, etiqueta).</summary>
    private static IEnumerable<(int N, string Input, string? Label)> Inputs(Hwmon h, string kind, string suffix = "_input")
    {
        var dir = Directory.EnumerateFiles(h.Dir, $"{kind}*{suffix}").Any() ? h.Dir : Path.Combine(h.Dir, "device");
        foreach (var f in Sys.Dirs(dir, $"{kind}*{suffix}"))
        {
            var file = Path.GetFileName(f);
            if (!int.TryParse(file[kind.Length..^suffix.Length], out var n)) continue;
            yield return (n, f, Sys.Read(Path.Combine(dir, $"{kind}{n}_label")));
        }
    }

    private static float? Milli(string path) => Sys.ReadLong(path) is { } v ? v / 1000f : null;

    // ---------- CPU ----------

    private static string ReadCpuName()
    {
        var info = Sys.Read("/proc/cpuinfo") ?? "";
        var m = Regex.Match(info, @"^(model name|Hardware|Model)\s*:\s*(.+)$", RegexOptions.Multiline);
        return m.Success ? Regex.Replace(m.Groups[2].Value.Trim(), @"\s+", " ") : "CPU";
    }

    private static readonly HashSet<string> CpuTempChips = new() { "coretemp", "k10temp", "zenpower", "cpu_thermal", "soc_thermal" };

    private HardwareDto? Cpu(HashSet<Hwmon> used)
    {
        const string id = "/cpu/0";
        var sensors = new List<SensorDto>();

        // Carga total y por núcleo (/proc/stat: user nice system idle iowait irq softirq steal)
        foreach (var line in (Sys.Read("/proc/stat") ?? "").Split('\n').Where(l => l.StartsWith("cpu")))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var nums = parts.Skip(1).Take(8).Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            if (nums.Length < 5) continue;
            var total = nums.Sum();
            var idle = nums[3] + nums[4];
            var key = "stat/" + parts[0];
            var dTotal = Rate(key + "/total", total);
            var dIdle = Rate(key + "/idle", idle);
            float? load = dTotal is > 0 && dIdle is { } di ? (float)Math.Clamp(100 * (1 - di / dTotal.Value), 0, 100) : null;
            if (parts[0] == "cpu") sensors.Add(Sensor(id, "CPU Total", "Load", load));
            else if (int.TryParse(parts[0][3..], out var n)) sensors.Add(Sensor(id, $"CPU Core #{n + 1}", "Load", load));
        }

        // Frecuencia de cada núcleo (cpufreq en kHz; en máquinas virtuales, la de /proc/cpuinfo)
        var cpuDirs = Sys.Dirs("/sys/devices/system/cpu", "cpu*").Where(d => Regex.IsMatch(Path.GetFileName(d), @"^cpu\d+$"))
            .OrderBy(d => int.Parse(Path.GetFileName(d)[3..])).ToList();
        var mhz = cpuDirs.Select(d => Sys.ReadLong(Path.Combine(d, "cpufreq", "scaling_cur_freq")) is { } khz ? khz / 1000f : (float?)null).ToList();
        if (mhz.All(v => v is null))
            mhz = Regex.Matches(Sys.Read("/proc/cpuinfo") ?? "", @"^cpu MHz\s*:\s*([\d.]+)", RegexOptions.Multiline).Select(m => Sys.ParseFloat(m.Groups[1].Value)).ToList();
        for (int i = 0; i < mhz.Count; i++)
            sensors.Add(Sensor(id, $"Core #{i + 1}", "Clock", mhz[i]));

        // Temperaturas: coretemp (Intel), k10temp/zenpower (AMD) o el sensor del SoC
        foreach (var h in _hwmons.Where(h => CpuTempChips.Contains(h.Name)))
        {
            used.Add(h);
            foreach (var (n, input, label) in Inputs(h, "temp"))
            {
                var name = label switch
                {
                    null => "CPU Package",
                    _ when label.StartsWith("Package id ") => label == "Package id 0" ? "CPU Package" : "CPU Package #" + (int.Parse(label[11..]) + 1),
                    _ when label.StartsWith("Core ") && int.TryParse(label[5..], out var c) => $"CPU Core #{c + 1}",
                    _ => label,
                };
                sensors.Add(Sensor(id, name, "Temperature", Milli(input), $"{h.Name}{n}"));
            }
        }
        // Sin sensor propio del CPU: la zona térmica del paquete o la de ACPI (como en Windows)
        if (!sensors.Any(s => s.Type == "Temperature"))
        {
            foreach (var zone in Sys.Dirs("/sys/class/thermal", "thermal_zone*"))
            {
                var type = Sys.Read(Path.Combine(zone, "type"));
                var name = type switch { "x86_pkg_temp" => "CPU Package", "acpitz" => "Zona térmica (ACPI)", _ => null };
                if (name is null || sensors.Any(s => s.Name == name)) continue;
                sensors.Add(Sensor(id, name, "Temperature", Milli(Path.Combine(zone, "temp"))));
            }
        }

        // Consumo del paquete por RAPL (microjoules acumulados; requiere root)
        foreach (var zone in Sys.Dirs("/sys/class/powercap", "intel-rapl:*").Where(z => Regex.IsMatch(Path.GetFileName(z), @"^intel-rapl:\d+$")))
        {
            var n = int.Parse(Path.GetFileName(zone)[11..]);
            var watts = Rate("rapl/" + n, Sys.ReadLong(Path.Combine(zone, "energy_uj")), 1e-6);
            sensors.Add(Sensor(id, n == 0 ? "Package" : $"Package #{n + 1}", "Power", (float?)watts));
        }

        return sensors.Count > 0 ? new HardwareDto(id, CpuName, "Cpu", sensors) : null;
    }

    // ---------- Memoria ----------

    private IEnumerable<HardwareDto> Memory()
    {
        var info = (Sys.Read("/proc/meminfo") ?? "").Split('\n')
            .Select(l => l.Split(':', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim(), p => long.TryParse(p[1].Trim().Split(' ')[0], out var kb) ? kb : 0);
        HardwareDto? Make(string id, string name, string totalKey, string freeKey)
        {
            if (!info.TryGetValue(totalKey, out var total) || total <= 0) return null;
            var free = info.GetValueOrDefault(freeKey);
            const float gb = 1024f * 1024f;
            return new HardwareDto(id, name, "Memory", new()
            {
                Sensor(id, "Memory", "Load", (total - free) * 100f / total),
                Sensor(id, "Memory Used", "Data", (total - free) / gb),
                Sensor(id, "Memory Available", "Data", free / gb),
            });
        }
        if (Make("/ram", "Memoria", "MemTotal", "MemAvailable") is { } ram) yield return ram;
        if (Make("/vram", "Memoria virtual (swap)", "SwapTotal", "SwapFree") is { } swap) yield return swap;
    }

    // ---------- GPU ----------

    private IEnumerable<HardwareDto> Gpus(HashSet<Hwmon> used)
    {
        var result = new List<HardwareDto>();
        var nvidia = NvidiaSmi();
        result.AddRange(nvidia);

        foreach (var card in Sys.Dirs("/sys/class/drm", "card*").Where(c => Regex.IsMatch(Path.GetFileName(c), @"^card\d+$")))
        {
            var dev = Path.Combine(card, "device");
            var vendor = Sys.Read(Path.Combine(dev, "vendor"));
            var type = vendor switch { "0x1002" => "GpuAmd", "0x8086" => "GpuIntel", "0x10de" => "GpuNvidia", _ => null };
            // Las NVIDIA con el driver propietario ya vienen de nvidia-smi
            if (type is null || (type == "GpuNvidia" && nvidia.Count > 0)) continue;
            var devPath = Sys.RealPath(dev);
            var slot = devPath is null ? "" : Path.GetFileName(devPath);
            var id = $"/gpu/{Path.GetFileName(card)}";
            var sensors = new List<SensorDto>();

            if (type == "GpuAmd")
            {
                sensors.Add(Sensor(id, "GPU Core", "Load", Sys.ReadLong(Path.Combine(dev, "gpu_busy_percent"))));
                if (Sys.ReadLong(Path.Combine(dev, "mem_info_vram_used")) is { } vu && Sys.ReadLong(Path.Combine(dev, "mem_info_vram_total")) is { } vt and > 0)
                {
                    sensors.Add(Sensor(id, "GPU Memory Used", "SmallData", vu / 1048576f));
                    sensors.Add(Sensor(id, "GPU Memory Total", "SmallData", vt / 1048576f));
                    sensors.Add(Sensor(id, "GPU Memory", "Load", vu * 100f / vt));
                }
            }
            else if (type == "GpuIntel")
            {
                // Frecuencia actual (i915 o xe)
                var freq = new[]
                {
                    Path.Combine(card, "gt_cur_freq_mhz"), Path.Combine(card, "gt", "gt0", "rps_cur_freq_mhz"),
                    Path.Combine(dev, "tile0", "gt0", "freq0", "act_freq"), Path.Combine(dev, "tile0", "gt0", "freq0", "cur_freq"),
                }.Select(Sys.ReadLong).FirstOrDefault(v => v is not null);
                sensors.Add(Sensor(id, "GPU Core", "Clock", freq));
                // Carga: el tiempo que no estuvo en reposo (RC6) desde la lectura anterior
                var idle = new[]
                {
                    Path.Combine(card, "gt", "gt0", "rc6_residency_ms"), Path.Combine(card, "power", "rc6_residency_ms"),
                    Path.Combine(dev, "tile0", "gt0", "gtidle", "idle_residency_ms"),
                }.Select(Sys.ReadLong).FirstOrDefault(v => v is not null);
                if (Rate($"{id}/idle", idle, 1e-3) is { } idleFraction)
                    sensors.Add(Sensor(id, "GPU Core", "Load", (float)Math.Clamp(100 - idleFraction * 100, 0, 100)));
                // Integrada: consumo del dominio "uncore" de RAPL
                foreach (var zone in Sys.Dirs("/sys/class/powercap", "intel-rapl:0:*"))
                    if (Sys.Read(Path.Combine(zone, "name")) == "uncore")
                        sensors.Add(Sensor(id, "GPU Package", "Power", (float?)Rate($"{id}/uncore", Sys.ReadLong(Path.Combine(zone, "energy_uj")), 1e-6)));
            }

            // Sensores hwmon del mismo dispositivo (amdgpu, i915 de placas Arc, nouveau)
            foreach (var h in _hwmons.Where(h => h.Device is not null && h.Device == devPath))
            {
                used.Add(h);
                foreach (var (n, input, label) in Inputs(h, "temp"))
                {
                    var name = label switch { "edge" or null => "GPU Core", "junction" => "GPU Hot Spot", "mem" => "GPU Memory", _ => "GPU " + label };
                    sensors.Add(Sensor(id, name, "Temperature", Milli(input), $"temp{n}"));
                }
                var power = Inputs(h, "power", "_average").Concat(Inputs(h, "power")).FirstOrDefault();
                if (power.Input is not null)
                    sensors.Add(Sensor(id, "GPU Package", "Power", Sys.ReadLong(power.Input) / 1e6f));
                else if (Inputs(h, "energy").FirstOrDefault() is { Input: not null } energy)
                    sensors.Add(Sensor(id, "GPU Package", "Power", (float?)Rate($"{id}/energy", Sys.ReadLong(energy.Input), 1e-6)));
                foreach (var (n, input, label) in Inputs(h, "freq"))
                    sensors.Add(Sensor(id, label == "mclk" ? "GPU Memory" : "GPU Core", "Clock", Sys.ReadLong(input) / 1e6f, $"freq{n}"));
                foreach (var (n, input, _) in Inputs(h, "fan"))
                    sensors.Add(Sensor(id, "GPU Fan", "Fan", Sys.ReadLong(input), $"fan{n}"));
            }

            result.Add(new HardwareDto(id, GpuName(slot, type), type, sensors));
        }
        return result;
    }

    // nvidia-smi tarda unos 50 ms: se consulta cada 2 s como máximo
    private List<HardwareDto> NvidiaSmi()
    {
        if ((DateTime.UtcNow - _nvidia.At).TotalSeconds < 2) return _nvidia.Gpus;
        var gpus = new List<HardwareDto>();
        if (Sys.Which("nvidia-smi") is { } smi)
        {
            var (code, output) = Sys.Run(smi, "--query-gpu=index,name,utilization.gpu,temperature.gpu,power.draw,clocks.gr,clocks.mem,memory.used,memory.total,fan.speed,utilization.memory",
                "--format=csv,noheader,nounits");
            if (code == 0)
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var f = line.Split(',').Select(s => s.Trim()).ToArray();
                    if (f.Length < 11) continue;
                    var id = $"/gpu-nvidia/{f[0]}";
                    gpus.Add(new HardwareDto(id, f[1], "GpuNvidia", new()
                    {
                        Sensor(id, "GPU Core", "Load", Sys.ParseFloat(f[2])),
                        Sensor(id, "GPU Core", "Temperature", Sys.ParseFloat(f[3])),
                        Sensor(id, "GPU Package", "Power", Sys.ParseFloat(f[4])),
                        Sensor(id, "GPU Core", "Clock", Sys.ParseFloat(f[5])),
                        Sensor(id, "GPU Memory", "Clock", Sys.ParseFloat(f[6])),
                        Sensor(id, "GPU Memory Used", "SmallData", Sys.ParseFloat(f[7])),
                        Sensor(id, "GPU Memory Total", "SmallData", Sys.ParseFloat(f[8])),
                        Sensor(id, "GPU Fan", "Control", Sys.ParseFloat(f[9])),
                        Sensor(id, "GPU Memory Controller", "Load", Sys.ParseFloat(f[10])),
                    }));
                }
        }
        _nvidia = (DateTime.UtcNow, gpus);
        return gpus;
    }

    // Nombre comercial desde lspci: "Intel Corporation" "CoffeeLake-S GT2 [UHD Graphics 630]" → "Intel UHD Graphics 630"
    private string GpuName(string slot, string type)
    {
        _pciNames ??= ReadPciNames();
        var brand = type switch { "GpuAmd" => "AMD", "GpuIntel" => "Intel", _ => "NVIDIA" };
        if (!_pciNames.TryGetValue(slot, out var device)) return $"{brand} Graphics";
        var bracket = Regex.Match(device, @"\[(.+)\]");
        return $"{brand} {(bracket.Success ? bracket.Groups[1].Value : device)}";
    }

    private static Dictionary<string, string> ReadPciNames()
    {
        var names = new Dictionary<string, string>();
        if (Sys.Which("lspci") is not { } lspci) return names;
        foreach (var line in Sys.Run(lspci, "-mmD").Output.Split('\n'))
        {
            var quoted = Regex.Matches(line, "\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToList();
            var slot = line.Split(' ')[0];
            if (quoted.Count >= 3) names[slot] = quoted[2];
        }
        return names;
    }

    // ---------- Discos ----------

    private static readonly string[] VirtualDisks = { "loop", "ram", "zram", "dm-", "md", "zd", "nbd", "sr", "fd", "rbd" };

    private IEnumerable<HardwareDto> Storage(HashSet<Hwmon> used)
    {
        var stats = (Sys.Read("/proc/diskstats") ?? "").Split('\n')
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(p => p.Length >= 14)
            .ToDictionary(p => p[2], p => p);
        foreach (var dir in Sys.Dirs("/sys/block"))
        {
            var dev = Path.GetFileName(dir);
            if (VirtualDisks.Any(dev.StartsWith) || !Directory.Exists(Path.Combine(dir, "device")) || !stats.TryGetValue(dev, out var s)) continue;
            var id = $"/storage/{dev}";
            double Field(int i) => double.Parse(s[i], CultureInfo.InvariantCulture);
            var sensors = new List<SensorDto>
            {
                // Sectores de 512 bytes; tiempo con E/S en curso en ms
                Sensor(id, "Read Rate", "Throughput", (float?)Rate(id + "/read", Field(5) * 512)),
                Sensor(id, "Write Rate", "Throughput", (float?)Rate(id + "/write", Field(9) * 512)),
                Sensor(id, "Total Activity", "Load", Rate(id + "/busy", Field(12), 0.1) is { } busy ? (float)Math.Min(busy, 100) : null),
                Sensor(id, "Data Read", "Data", (float)(Field(5) * 512 / 1e9)),
                Sensor(id, "Data Written", "Data", (float)(Field(9) * 512 / 1e9)),
            };
            var devPath = Sys.RealPath(Path.Combine(dir, "device"));
            foreach (var h in _hwmons.Where(h => h.Device is not null && h.Device == devPath))
            {
                used.Add(h);
                foreach (var (n, input, label) in Inputs(h, "temp"))
                    sensors.Insert(0, Sensor(id, n == 1 ? "Temperature" : label ?? $"Temperature #{n}", "Temperature", Milli(input), $"temp{n}"));
            }
            var model = Sys.Read(Path.Combine(dir, "device", "model"));
            yield return new HardwareDto(id, string.IsNullOrWhiteSpace(model) ? dev : $"{model} ({dev})", "Storage", sensors);
        }
    }

    // ---------- Red ----------

    private IEnumerable<HardwareDto> Network()
    {
        foreach (var line in (Sys.Read("/proc/net/dev") ?? "").Split('\n').Skip(2))
        {
            var parts = line.Split(':', 2);
            if (parts.Length != 2) continue;
            var name = parts[0].Trim();
            // Solo placas físicas (cable o Wi-Fi) conectadas: los puentes y las interfaces virtuales repetirían el tráfico
            var sys = Path.Combine("/sys/class/net", name);
            if (!Directory.Exists(Path.Combine(sys, "device")) || Sys.Read(Path.Combine(sys, "operstate")) is not ("up" or "unknown")) continue;
            var f = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            if (f.Length < 9) continue;
            var id = $"/nic/{name}";
            var down = Rate(id + "/rx", f[0]);
            var up = Rate(id + "/tx", f[8]);
            var sensors = new List<SensorDto>
            {
                Sensor(id, "Upload Speed", "Throughput", (float?)up),
                Sensor(id, "Download Speed", "Throughput", (float?)down),
                Sensor(id, "Data Uploaded", "Data", (float)(f[8] / 1e9)),
                Sensor(id, "Data Downloaded", "Data", (float)(f[0] / 1e9)),
            };
            if (Sys.ReadLong(Path.Combine(sys, "speed")) is > 0 and var mbps && up is not null && down is not null)
                sensors.Add(Sensor(id, "Network Utilization", "Load", (float)Math.Min(100, Math.Max(up.Value, down.Value) * 8 / (mbps * 1e6) * 100)));
            yield return new HardwareDto(id, name, "Network", sensors);
        }
    }

    // ---------- Placa (chips hwmon restantes) ----------

    private static readonly string[] SkipChips = { "acpitz", "nvme", "drivetemp", "amdgpu", "i915", "xe", "nouveau", "BAT", "AC", "ADP", "ucsi", "hidpp", "iwlwifi", "mt7", "ath" };

    private IEnumerable<HardwareDto> Chips(HashSet<Hwmon> used)
    {
        foreach (var h in _hwmons)
        {
            if (used.Contains(h) || SkipChips.Any(s => h.Name.StartsWith(s, StringComparison.Ordinal))) continue;
            var id = $"/lpc/{h.Name}/{Path.GetFileName(h.Dir)}";
            var sensors = new List<SensorDto>();
            foreach (var (n, input, label) in Inputs(h, "temp"))
                sensors.Add(Sensor(id, label ?? $"Temperature #{n}", "Temperature", Milli(input), $"temp{n}"));
            foreach (var (n, input, label) in Inputs(h, "fan"))
            {
                // Los conectores sin ventilador marcan 0: se muestran recién cuando giran alguna vez
                var rpm = Sys.ReadLong(input);
                var key = $"{id}/fan{n}";
                if (rpm > 0) _fansSeen.Add(key);
                if (_fansSeen.Contains(key)) sensors.Add(Sensor(id, label ?? $"Fan #{n}", "Fan", rpm, $"fan{n}"));
            }
            foreach (var (n, input, label) in Inputs(h, "in"))
                sensors.Add(Sensor(id, label ?? $"Voltage #{n}", "Voltage", Milli(input), $"in{n}"));
            foreach (var (n, pwm, _) in Inputs(h, "pwm", ""))
            {
                var value = Sys.ReadLong(pwm) * 100f / 255;
                var name = $"Fan Control #{n}";
                sensors.Add(Sensor(id, name, "Control", value, $"pwm{n}"));
                var enable = pwm + "_enable";
                if (File.Exists(enable)) _fanControls.TryAdd($"{id}/pwm{n}", new FanControl($"{id}/pwm{n}", name, h.Name, pwm, enable));
            }
            if (sensors.Count > 0) yield return new HardwareDto(id, h.Name, "SuperIO", sensors);
        }
    }

    // ---------- Batería ----------

    private IEnumerable<HardwareDto> Batteries()
    {
        foreach (var dir in Sys.Dirs("/sys/class/power_supply"))
        {
            // Solo baterías del equipo: "Device" son las de mouse o auriculares
            if (Sys.Read(Path.Combine(dir, "type")) != "Battery" || Sys.Read(Path.Combine(dir, "scope")) == "Device") continue;
            var id = $"/battery/{Path.GetFileName(dir)}";
            float? R(string f) => Sys.ReadLong(Path.Combine(dir, f));
            var status = Sys.Read(Path.Combine(dir, "status"));
            // µW, o µA × µV en las que informan carga en lugar de energía
            var watts = R("power_now") is { } pw ? pw / 1e6f : R("current_now") * R("voltage_now") / 1e12f;
            var full = R("energy_full") ?? R("charge_full");
            var design = R("energy_full_design") ?? R("charge_full_design");
            var now = R("energy_now") ?? R("charge_now");
            var sensors = new List<SensorDto>
            {
                Sensor(id, "Charge Level", "Level", R("capacity")),
                Sensor(id, "Voltage", "Voltage", R("voltage_now") / 1e6f),
            };
            if (watts is > 0)
                sensors.Add(Sensor(id, status == "Charging" ? "Charge Rate" : "Discharge Rate", "Power", watts));
            if (full is > 0 && design is > 0)
                sensors.Add(Sensor(id, "Degradation Level", "Level", Math.Max(0, 100 - full.Value * 100 / design.Value)));
            if (status == "Discharging" && R("energy_now") is { } en && R("power_now") is > 0 and var p)
                sensors.Add(Sensor(id, "Remaining Time (Estimated)", "TimeSpan", en / p * 3600));
            if (now is not null && R("energy_now") is not null)
                sensors.Add(Sensor(id, "Remaining Capacity", "Energy", now / 1000f));
            var model = Sys.Read(Path.Combine(dir, "model_name"));
            yield return new HardwareDto(id, string.IsNullOrWhiteSpace(model) ? "Batería" : model, "Battery", sensors);
        }
    }

    // ---------- Ventiladores de la placa ----------

    public List<FanControlDto> GetFanControls()
    {
        lock (_lock)
        {
            if (!Sys.IsRoot) return new();
            return _fanControls.Values.Select(f =>
            {
                var manual = Sys.Read(f.EnablePath) == "1";
                return new FanControlDto(f.Id, f.Name, f.Hardware, manual ? "Software" : "Default",
                    manual ? MathF.Round(Sys.ReadLong(f.PwmPath) * 100f / 255 ?? 0) : null, 0, 100);
            }).ToList();
        }
    }

    public bool SetFan(string id, float? percent)
    {
        lock (_lock)
        {
            if (!_fanControls.TryGetValue(id, out var f)) return false;
            // Se recuerda el modo original (automático del chip) para volver a él
            if (!_fanOriginalMode.ContainsKey(id)) _fanOriginalMode[id] = Sys.Read(f.EnablePath) ?? "2";
            if (percent is null)
                return Sys.Write(f.EnablePath, _fanOriginalMode[id] == "1" ? "2" : _fanOriginalMode[id]);
            var pwm = (int)Math.Round(Math.Clamp(percent.Value, 0, 100) * 2.55);
            return Sys.Write(f.EnablePath, "1") && Sys.Write(f.PwmPath, pwm.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Devuelve los ventiladores tocados a su modo original al cerrar.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var (id, mode) in _fanOriginalMode)
                if (_fanControls.TryGetValue(id, out var f)) Sys.Write(f.EnablePath, mode == "1" ? "2" : mode);
        }
    }
}
