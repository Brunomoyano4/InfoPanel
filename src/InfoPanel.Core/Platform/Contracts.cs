using InfoPanel.Controls;
using InfoPanel.Services;

// Lo que cada sistema operativo implementa a su manera. InfoPanel.Windows e InfoPanel.Linux registran
// sus implementaciones; el servidor (InfoPanelServer) y la telemetría (TelemetryLoop) solo usan estas interfaces.
// Los datos viajan con la misma forma en los dos sistemas, así la web no distingue de dónde vienen.

namespace InfoPanel.Services
{
    // Sensores con los nombres y tipos de LibreHardwareMonitor: la web busca, por ejemplo, "CPU Total" (Load)
    // en el hardware de tipo "Cpu". En Linux se arman con los mismos nombres.
    public record SensorDto(string Id, string Name, string Type, float? Value, float? Min, float? Max);
    public record HardwareDto(string Id, string Name, string Type, List<SensorDto> Sensors);
    public record FanControlDto(string Id, string Name, string Hardware, string Mode, float? Value, float Min, float Max);

    public record RamModuleDto(string Slot, string Manufacturer, string Part, long SizeGb, int SpeedMts);
    public record GpuInfoDto(string Name, string Driver);
    // Windows: la versión del sistema operativo (en Linux, la distribución y el kernel); el nombre queda por compatibilidad
    public record SystemInfoDto(string Windows, string Bios, string Board, List<RamModuleDto> Ram, List<GpuInfoDto> Gpus);

    public record ProcessDto(int Pid, string Name, double Cpu, long MemoryMb);
    public record DiskDto(string Name, string Label, string Format, long TotalGb, long FreeGb);
    // OnAc: true enchufada, false a batería, null sin dato (PC de escritorio o estado desconocido)
    public record SystemDto(long UptimeSec, List<ProcessDto> Processes, List<DiskDto> Disks, bool? OnAc, bool Charging);
}

namespace InfoPanel.Controls
{
    public record PowerPlanDto(string Guid, string Name, bool Active);
    public record AudioDeviceDto(string Id, string Name, bool Default);
    public record SystemControlStateDto(int? Volume, bool? Muted, int? Brightness, string PowerMode, List<PowerPlanDto> PowerPlans,
        bool? MicMuted, List<AudioDeviceDto> Outputs);

    public record TdpDto(string Backend, int Min, int Max, int? Spl, int? Sppt, int? Fppt);

    public record AsusFanDto(string Fan, int Rpm);
    public record FanCurveDto(string Fan, int[] Temps, int[] Speeds);
    public record AsusStateDto(bool Available, int Mode, string ModeName, List<AsusFanDto> Fans);

    public record DisplayDto(string Name, bool Internal, int Width, int Height, double RefreshHz, int X, int Y, int Rotation, bool Primary, string? Device = null);
    public record DisplayModeDto(int Width, int Height, List<int> Hz);
    public record DisplayPresetDto(string Name, List<DisplayDto> Displays);
    // Mapa de pantallas: Id identifica la salida aunque esté apagada; X/Y/Width/Height en px del escritorio
    public record LayoutDisplayDto(string Id, string Name, bool Internal, bool Active, bool Primary, int X, int Y, int Width, int Height);
    public record LayoutPlacement(string Id, int X, int Y);

    public record AppDto(string Id, string Name);
}

namespace InfoPanel.Platform
{
    /// <summary>
    /// Qué ofrece este sistema, para que la web muestre solo los controles que funcionan.
    /// Os: "windows" o "linux". PowerActions: lock, sleep, hibernate, restart, shutdown (las disponibles).
    /// </summary>
    public record PlatformCapsDto(string Os, bool PowerPlans, bool Displays, bool Media, List<string> PowerActions, string AutoStartLabel);

    public interface IPlatformInfo
    {
        PlatformCapsDto Caps { get; }
    }

    /// <summary>Sensores de todo el hardware y ventiladores controlables.</summary>
    public interface IHardwareMonitor : IDisposable
    {
        string CpuName { get; }
        List<HardwareDto> Update();
        List<FanControlDto> GetFanControls();
        /// <summary>Fija un ventilador en porcentaje, o lo devuelve al control automático si percent es null.</summary>
        bool SetFan(string id, float? percent);
    }

    /// <summary>Procesos, espacio en disco, uptime, estado de la alimentación y datos fijos del equipo.</summary>
    public interface ISystemMonitor
    {
        SystemDto Sample();
        SystemInfoDto ReadInfo();
    }

    /// <summary>Volumen, micrófono, brillo, multimedia, energía y procesos.</summary>
    public interface ISystemControl
    {
        SystemControlStateDto GetState();
        bool SetVolume(int? volume, bool? mute);
        bool SetOutput(string deviceId);
        bool SetMicMuted(bool mute);
        bool SetBrightness(int value);
        bool Media(string action);
        bool Power(string action);
        bool SetPowerMode(string mode);
        bool SetPowerPlan(string id);
        bool KillProcess(int pid);
    }

    /// <summary>Modos de rendimiento, curvas de ventilador y límite de carga de equipos ASUS.</summary>
    public interface IAsusControl
    {
        bool Available { get; }
        int? BatteryLimit { get; }
        AsusStateDto GetState();
        bool SetMode(int mode);
        List<FanCurveDto> GetCurves(int? mode = null);
        string? SetCurve(FanCurveDto curve);
        string? SetBatteryLimit(int percent);
    }

    /// <summary>Límites de potencia en watts. Backend "none" si no hay forma de cambiarlos.</summary>
    public interface ITdpControl
    {
        string Backend { get; }
        TdpDto GetState();
        string? Set(int spl, int sppt, int fppt);
    }

    /// <summary>Pantallas: resolución, Hz, cuáles están prendidas, ubicación, modos y presets.</summary>
    public interface IDisplayControl
    {
        List<DisplayDto> GetDisplays();
        Dictionary<string, List<DisplayModeDto>> GetModes();
        List<DisplayPresetDto> GetPresets();
        List<LayoutDisplayDto> GetLayout();
        string? SetMode(string device, int width, int height, int hz);
        string? SetActive(IReadOnlyCollection<string> ids);
        string? SetPositions(IReadOnlyList<LayoutPlacement> placements, string primaryId);
        bool SetTopology(string topology);
        string? SavePreset(string name);
        string? ApplyPreset(string name);
        bool DeletePreset(string name);
    }

    /// <summary>Inicio automático con el sistema.</summary>
    public interface IAutoStart
    {
        bool IsEnabled { get; }
        string? SetEnabled(bool enabled);
    }

    /// <summary>Aplicaciones instaladas que se pueden abrir desde el panel (solo de la lista, nunca comandos).</summary>
    public interface IAppLauncher
    {
        List<AppDto> GetApps(bool refresh = false);
        byte[]? GetIcon(string id);
        bool Launch(string id);
    }

    /// <summary>
    /// Cómo se reemplaza y se reinicia el ejecutable al actualizar. AssetName es el archivo del release
    /// para este sistema; Relaunch arranca la versión nueva (o no hace nada si el sistema la reinicia solo).
    /// </summary>
    public interface IUpdatePlatform
    {
        string AssetName { get; }
        void Relaunch(string exePath);
    }

    /// <summary>Pantallas sin control (sistema sin escritorio o sin soporte): todo vacío y con un error claro.</summary>
    public sealed class NoDisplayControl : IDisplayControl
    {
        private const string Error = "El control de pantallas no está disponible en este sistema";
        public List<DisplayDto> GetDisplays() => new();
        public Dictionary<string, List<DisplayModeDto>> GetModes() => new();
        public List<DisplayPresetDto> GetPresets() => new();
        public List<LayoutDisplayDto> GetLayout() => new();
        public string? SetMode(string device, int width, int height, int hz) => Error;
        public string? SetActive(IReadOnlyCollection<string> ids) => Error;
        public string? SetPositions(IReadOnlyList<LayoutPlacement> placements, string primaryId) => Error;
        public bool SetTopology(string topology) => false;
        public string? SavePreset(string name) => Error;
        public string? ApplyPreset(string name) => Error;
        public bool DeletePreset(string name) => false;
    }

    /// <summary>Equipo que no es ASUS (o sin acceso a su firmware).</summary>
    public sealed class NoAsusControl : IAsusControl
    {
        private const string Error = "ACPI de ASUS no disponible";
        public bool Available => false;
        public int? BatteryLimit => null;
        public AsusStateDto GetState() => new(false, 0, "", new());
        public bool SetMode(int mode) => false;
        public List<FanCurveDto> GetCurves(int? mode = null) => new();
        public string? SetCurve(FanCurveDto curve) => Error;
        public string? SetBatteryLimit(int percent) => Error;
    }

    /// <summary>Sin forma de cambiar los límites de potencia.</summary>
    public sealed class NoTdpControl : ITdpControl
    {
        public string Backend => "none";
        public TdpDto GetState() => new("none", 0, 0, null, null, null);
        public string? Set(int spl, int sppt, int fppt) => "No hay forma de controlar el TDP en este equipo";
    }

    /// <summary>Sin lista de aplicaciones (por ejemplo, un servidor sin escritorio).</summary>
    public sealed class NoAppLauncher : IAppLauncher
    {
        public List<AppDto> GetApps(bool refresh = false) => new();
        public byte[]? GetIcon(string id) => null;
        public bool Launch(string id) => false;
    }
}
