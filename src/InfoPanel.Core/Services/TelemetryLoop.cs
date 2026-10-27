using System.Text.Json;
using InfoPanel.Platform;

namespace InfoPanel.Services;

/// <summary>Cada segundo lee todo y lo envía a los clientes conectados.</summary>
public sealed class TelemetryLoop : BackgroundService
{
    private readonly IHardwareMonitor _hw;
    private readonly ISystemMonitor _sys;
    private readonly ISystemControl _sysCtl;
    private readonly IAsusControl _asus;
    private readonly ITdpControl _tdp;
    private readonly IDisplayControl _display;
    private readonly IAutoStart _autoStart;
    private readonly IPlatformInfo _platform;
    private readonly UpdateService _update;
    private readonly TelemetryHub _hub;
    private readonly JsonSerializerOptions _json;
    private readonly ILogger<TelemetryLoop> _log;

    private object? _controls;
    private DateTime _controlsRead = DateTime.MinValue;

    public TelemetryLoop(IHardwareMonitor hw, ISystemMonitor sys, ISystemControl sysCtl, IAsusControl asus, ITdpControl tdp,
        IDisplayControl display, IAutoStart autoStart, IPlatformInfo platform, UpdateService update, TelemetryHub hub,
        JsonSerializerOptions json, ILogger<TelemetryLoop> log)
    {
        (_hw, _sys, _sysCtl, _asus, _tdp, _hub, _json, _log) = (hw, sys, sysCtl, asus, tdp, hub, json, log);
        (_display, _autoStart, _platform, _update) = (display, autoStart, platform, update);
    }

    /// <summary>Fuerza a releer el estado de los controles en el próximo ciclo (después de un cambio).</summary>
    public void InvalidateControls() => _controlsRead = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            if (_hub.ClientCount == 0) continue;
            try
            {
                // El estado de los controles cambia poco: se relee cada 3 s
                if ((DateTime.UtcNow - _controlsRead).TotalSeconds >= 3)
                {
                    _controls = new
                    {
                        caps = _platform.Caps,
                        system = _sysCtl.GetState(),
                        tdp = _tdp.GetState(),
                        lhmFans = _hw.GetFanControls(),
                        displays = _display.GetDisplays(),
                        displayModes = _display.GetModes(),
                        displayPresets = _display.GetPresets(),
                        displayLayout = _display.GetLayout(),
                        autoStart = _autoStart.IsEnabled,
                        batteryLimit = _asus.BatteryLimit,
                        update = _update.State,
                    };
                    _controlsRead = DateTime.UtcNow;
                }

                var payload = new
                {
                    time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    hardware = _hw.Update(),
                    system = _sys.Sample(),
                    asus = _asus.GetState(),
                    controls = _controls,
                };
                await _hub.BroadcastAsync(payload, _json);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Error leyendo telemetría");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
