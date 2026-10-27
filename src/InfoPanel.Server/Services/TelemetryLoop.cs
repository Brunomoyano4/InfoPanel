using System.Text.Json;
using InfoPanel.Controls;

namespace InfoPanel.Services;

/// <summary>Cada segundo lee todo y lo envía a los clientes conectados.</summary>
public sealed class TelemetryLoop : BackgroundService
{
    private readonly HardwareService _hw;
    private readonly SystemService _sys;
    private readonly SystemControl _sysCtl;
    private readonly AsusControl _asus;
    private readonly TdpControl _tdp;
    private readonly DisplayControl _display;
    private readonly CpuFallbackService _cpuFallback;
    private readonly AutoStartService _autoStart;
    private readonly TelemetryHub _hub;
    private readonly JsonSerializerOptions _json;
    private readonly ILogger<TelemetryLoop> _log;

    private object? _controls;
    private DateTime _controlsRead = DateTime.MinValue;

    public TelemetryLoop(HardwareService hw, SystemService sys, SystemControl sysCtl, AsusControl asus, TdpControl tdp,
        DisplayControl display, AutoStartService autoStart, CpuFallbackService cpuFallback, TelemetryHub hub,
        JsonSerializerOptions json, ILogger<TelemetryLoop> log)
    {
        (_hw, _sys, _sysCtl, _asus, _tdp, _hub, _json, _log) = (hw, sys, sysCtl, asus, tdp, hub, json, log);
        (_display, _autoStart, _cpuFallback) = (display, autoStart, cpuFallback);
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
                        system = _sysCtl.GetState(),
                        tdp = _tdp.GetState(),
                        lhmFans = _hw.GetFanControls(),
                        displays = _display.GetDisplays(),
                        displayModes = _display.GetModes(),
                        displayPresets = _display.GetPresets(),
                        autoStart = _autoStart.IsEnabled,
                        batteryLimit = _asus.BatteryLimit,
                    };
                    _controlsRead = DateTime.UtcNow;
                }

                var hardware = _hw.Update();
                _cpuFallback.Augment(hardware);

                var payload = new
                {
                    time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    hardware,
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
