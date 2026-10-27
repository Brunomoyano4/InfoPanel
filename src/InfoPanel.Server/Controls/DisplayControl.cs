using System.Runtime.InteropServices;
using System.Text.Json;
using InfoPanel.Native;

namespace InfoPanel.Controls;

public record DisplayDto(string Name, bool Internal, int Width, int Height, double RefreshHz, int X, int Y, int Rotation, bool Primary, string? Device = null);
public record DisplayModeDto(int Width, int Height, List<int> Hz);
public record DisplayPresetDto(string Name, List<DisplayDto> Displays);

/// <summary>
/// Pantallas conectadas y presets de configuración (orden/posición, resolución, Hz, principal, cuáles activas).
/// Un preset guarda la configuración CCD completa de Windows y la vuelve a aplicar tal cual.
/// </summary>
public sealed class DisplayControl
{
    private static readonly string PresetsPath = Path.Combine(AppContext.BaseDirectory, "display-presets.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private class StoredPreset
    {
        public string Name { get; set; } = "";
        public List<DisplayDto> Displays { get; set; } = new();
        public string Paths { get; set; } = "";
        public string Modes { get; set; } = "";
    }

    public static readonly Dictionary<string, uint> Topologies = new()
    {
        ["internal"] = DisplayConfig.SDC_TOPOLOGY_INTERNAL,
        ["clone"] = DisplayConfig.SDC_TOPOLOGY_CLONE,
        ["extend"] = DisplayConfig.SDC_TOPOLOGY_EXTEND,
        ["external"] = DisplayConfig.SDC_TOPOLOGY_EXTERNAL,
    };

    private readonly object _lock = new();
    private List<StoredPreset> _presets;

    public DisplayControl()
    {
        _presets = File.Exists(PresetsPath)
            ? JsonSerializer.Deserialize<List<StoredPreset>>(File.ReadAllText(PresetsPath)) ?? new()
            : new();
    }

    public List<DisplayPresetDto> GetPresets()
    {
        lock (_lock) return _presets.Select(p => new DisplayPresetDto(p.Name, p.Displays)).ToList();
    }

    public List<DisplayDto> GetDisplays()
    {
        var (paths, modes) = Query(DisplayConfig.QDC_ONLY_ACTIVE_PATHS);
        return Describe(paths, modes);
    }

    public string? SavePreset(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return "Falta el nombre";
        var (paths, modes) = Query(DisplayConfig.QDC_ONLY_ACTIVE_PATHS);
        if (paths.Length == 0) return "No se pudo leer la configuración de pantallas";

        lock (_lock)
        {
            _presets.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            _presets.Add(new StoredPreset
            {
                Name = name,
                Displays = Describe(paths, modes),
                Paths = Convert.ToBase64String(MemoryMarshal.AsBytes(paths.AsSpan())),
                Modes = Convert.ToBase64String(MemoryMarshal.AsBytes(modes.AsSpan())),
            });
            Save();
        }
        return null;
    }

    public bool DeletePreset(string name)
    {
        lock (_lock)
        {
            var removed = _presets.RemoveAll(p => p.Name == name) > 0;
            if (removed) Save();
            return removed;
        }
    }

    public string? ApplyPreset(string name)
    {
        StoredPreset? preset;
        lock (_lock) preset = _presets.FirstOrDefault(p => p.Name == name);
        if (preset is null) return "Preset inexistente";

        var paths = MemoryMarshal.Cast<byte, PathInfo>(Convert.FromBase64String(preset.Paths)).ToArray();
        var modes = MemoryMarshal.Cast<byte, ModeInfo>(Convert.FromBase64String(preset.Modes)).ToArray();
        RemapAdapters(paths, modes);

        const uint flags = DisplayConfig.SDC_USE_SUPPLIED_DISPLAY_CONFIG | DisplayConfig.SDC_ALLOW_CHANGES;
        if (DisplayConfig.SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, flags | DisplayConfig.SDC_VALIDATE) != 0)
            return "La configuración no es válida con las pantallas conectadas ahora";
        var r = DisplayConfig.SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes,
            flags | DisplayConfig.SDC_APPLY | DisplayConfig.SDC_SAVE_TO_DATABASE);
        return r == 0 ? null : $"Windows rechazó la configuración (código {r})";
    }

    // Los modos de cada pantalla no cambian mientras esté conectada: se cachean por dispositivo
    private readonly Dictionary<string, List<DisplayModeDto>> _modesCache = new();

    /// <summary>Resoluciones y frecuencias disponibles de cada pantalla activa, por nombre GDI.</summary>
    public Dictionary<string, List<DisplayModeDto>> GetModes()
    {
        var result = new Dictionary<string, List<DisplayModeDto>>();
        foreach (var d in GetDisplays())
        {
            if (d.Device is null) continue;
            lock (_lock)
            {
                if (!_modesCache.TryGetValue(d.Device, out var modes))
                    _modesCache[d.Device] = modes = EnumModes(d.Device);
                result[d.Device] = modes;
            }
        }
        return result;
    }

    private static List<DisplayModeDto> EnumModes(string device)
    {
        var all = new List<(int W, int H, int Hz)>();
        var dm = new DEVMODE { Size = (short)Marshal.SizeOf<DEVMODE>() };
        for (int i = 0; DisplayConfig.EnumDisplaySettings(device, i, ref dm); i++)
            if (dm.BitsPerPel >= 32 && dm.DisplayFrequency > 1)
                all.Add((dm.PelsWidth, dm.PelsHeight, dm.DisplayFrequency));

        return all.GroupBy(m => (m.W, m.H))
            .Select(g => new DisplayModeDto(g.Key.W, g.Key.H, g.Select(m => m.Hz).Distinct().OrderByDescending(h => h).ToList()))
            .OrderByDescending(m => m.Width * m.Height)
            .ToList();
    }

    /// <summary>Cambia resolución y frecuencia de una pantalla y lo guarda como configuración de Windows.</summary>
    public string? SetMode(string device, int width, int height, int hz)
    {
        var dm = new DEVMODE { Size = (short)Marshal.SizeOf<DEVMODE>() };
        if (!DisplayConfig.EnumDisplaySettings(device, DisplayConfig.ENUM_CURRENT_SETTINGS, ref dm)) return "Pantalla no encontrada";

        List<DisplayModeDto>? modes;
        lock (_lock) _modesCache.TryGetValue(device, out modes);
        modes ??= EnumModes(device);
        if (!modes.Any(m => m.Width == width && m.Height == height && m.Hz.Contains(hz)))
            return "La pantalla no admite ese modo";

        dm.PelsWidth = width;
        dm.PelsHeight = height;
        dm.DisplayFrequency = hz;
        dm.Fields = DisplayConfig.DM_PELSWIDTH | DisplayConfig.DM_PELSHEIGHT | DisplayConfig.DM_DISPLAYFREQUENCY;
        var r = DisplayConfig.ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, DisplayConfig.CDS_UPDATEREGISTRY, IntPtr.Zero);
        return r == 0 ? null : $"Windows rechazó el modo (código {r})";
    }

    /// <summary>Equivalente a Win+P: internal, clone, extend, external.</summary>
    public bool SetTopology(string topology) =>
        Topologies.TryGetValue(topology, out var t) &&
        DisplayConfig.SetDisplayConfig(0, null, 0, null, DisplayConfig.SDC_APPLY | t) == 0;

    /// <summary>
    /// Los identificadores (LUID) de los adaptadores cambian entre reinicios: se reemplazan por los actuales
    /// buscando cada pantalla guardada por su id de destino, que sí es estable.
    /// </summary>
    private static void RemapAdapters(PathInfo[] paths, ModeInfo[] modes)
    {
        var (current, _) = Query(DisplayConfig.QDC_ALL_PATHS);
        var map = new Dictionary<(uint, int), LUID>();
        foreach (var p in paths)
        {
            var idx = Array.FindIndex(current, c => c.Target.Id == p.Target.Id);
            if (idx >= 0)
                map[(p.Target.AdapterId.Low, p.Target.AdapterId.High)] = current[idx].Target.AdapterId;
        }

        LUID Fix(LUID l) => map.TryGetValue((l.Low, l.High), out var n) ? n : l;
        for (int i = 0; i < paths.Length; i++)
        {
            paths[i].Source.AdapterId = Fix(paths[i].Source.AdapterId);
            paths[i].Target.AdapterId = Fix(paths[i].Target.AdapterId);
        }
        for (int i = 0; i < modes.Length; i++)
            modes[i].AdapterId = Fix(modes[i].AdapterId);
    }

    private static (PathInfo[] Paths, ModeInfo[] Modes) Query(uint flags)
    {
        if (DisplayConfig.GetDisplayConfigBufferSizes(flags, out var np, out var nm) != 0) return (Array.Empty<PathInfo>(), Array.Empty<ModeInfo>());
        var paths = new PathInfo[np];
        var modes = new ModeInfo[nm];
        if (DisplayConfig.QueryDisplayConfig(flags, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return (Array.Empty<PathInfo>(), Array.Empty<ModeInfo>());
        return (paths[..(int)np], modes[..(int)nm]);
    }

    private static List<DisplayDto> Describe(PathInfo[] paths, ModeInfo[] modes)
    {
        var list = new List<DisplayDto>();
        foreach (var p in paths)
        {
            var src = p.Source.ModeInfoIdx < modes.Length ? modes[p.Source.ModeInfoIdx].Source : default;
            var hz = p.Target.RefreshRate.Denominator == 0 ? 0 : Math.Round((double)p.Target.RefreshRate.Numerator / p.Target.RefreshRate.Denominator, 1);

            var info = new TargetDeviceName
            {
                Type = DisplayConfig.GET_TARGET_NAME,
                Size = (uint)Marshal.SizeOf<TargetDeviceName>(),
                AdapterId = p.Target.AdapterId,
                Id = p.Target.Id,
            };
            var name = DisplayConfig.DisplayConfigGetDeviceInfo(ref info) == 0 && !string.IsNullOrWhiteSpace(info.FriendlyName)
                ? info.FriendlyName : $"Pantalla {list.Count + 1}";
            var isInternal = p.Target.OutputTechnology == DisplayConfig.OUTPUT_INTERNAL;
            if (isInternal && name.StartsWith("Pantalla")) name = "Pantalla interna";

            // Nombre GDI (\\.\DISPLAY1), necesario para cambiar resolución y Hz
            var source = new SourceDeviceName
            {
                Type = DisplayConfig.GET_SOURCE_NAME,
                Size = (uint)Marshal.SizeOf<SourceDeviceName>(),
                AdapterId = p.Source.AdapterId,
                Id = p.Source.Id,
            };
            var device = DisplayConfig.DisplayConfigGetDeviceInfo(ref source) == 0 ? source.GdiDeviceName : null;

            // Rotación CCD: 1 = 0°, 2 = 90°, 3 = 180°, 4 = 270°
            var rotation = p.Target.Rotation is >= 1 and <= 4 ? (int)(p.Target.Rotation - 1) * 90 : 0;
            list.Add(new DisplayDto(name, isInternal, (int)src.Width, (int)src.Height, hz, src.X, src.Y, rotation, src.X == 0 && src.Y == 0, device));
        }
        return list.OrderBy(d => d.X).ThenBy(d => d.Y).ToList();
    }

    private void Save() => File.WriteAllText(PresetsPath, JsonSerializer.Serialize(_presets, JsonOpts));
}
