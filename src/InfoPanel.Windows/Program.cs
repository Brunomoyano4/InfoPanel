using InfoPanel;
using InfoPanel.Controls;
using InfoPanel.Platform;
using InfoPanel.Services;

// Una sola instancia: dos procesos chocarían por el puerto y por el hardware
using var instance = new Mutex(true, @"Global\InfoPanel", out var isFirst);
// Recién actualizado: la versión anterior todavía está cerrándose, se espera a que suelte la instancia
if (!isFirst && args.Contains(UpdateService.AfterUpdateArg))
{
    try { isFirst = instance.WaitOne(TimeSpan.FromSeconds(30)); }
    catch (AbandonedMutexException) { isFirst = true; } // terminó sin liberarla: igual queda tomada
}
if (!isFirst)
{
    System.Windows.Forms.MessageBox.Show("InfoPanel ya está en ejecución (ícono en la bandeja).", "InfoPanel");
    return;
}

var silent = args.Contains(AutoStartService.Arg);
var auth = new AuthService();
var certs = new CertificateService();

var app = InfoPanelServer.Build(args, auth, certs, services =>
{
    var platform = new WindowsPlatform();
    services.AddSingleton<IPlatformInfo>(platform);
    services.AddSingleton<IUpdatePlatform>(platform);
    services.AddSingleton<CpuFallbackService>();
    services.AddSingleton<IHardwareMonitor, HardwareService>();
    services.AddSingleton<ISystemMonitor, SystemService>();
    services.AddSingleton<ISystemControl, SystemControl>();
    services.AddSingleton<AsusAcpi>();
    services.AddSingleton<AsusControl>();
    services.AddSingleton<IAsusControl>(sp => sp.GetRequiredService<AsusControl>());
    services.AddSingleton<ITdpControl, TdpControl>();
    services.AddSingleton<IDisplayControl, DisplayControl>();
    services.AddSingleton<AutoStartService>();
    services.AddSingleton<IAutoStart>(sp => sp.GetRequiredService<AutoStartService>());
    services.AddSingleton<IAppLauncher, AppLauncher>();
});

try
{
    await app.StartAsync();
}
catch (Exception ex)
{
    System.Windows.Forms.MessageBox.Show($"No se pudo iniciar InfoPanel:\n{ex.Message}", "InfoPanel");
    return;
}

// WinForms necesita un hilo STA propio; al elegir "Salir" se detiene el servidor
var tray = new Thread(() =>
{
    System.Windows.Forms.Application.EnableVisualStyles();
    System.Windows.Forms.Application.Run(new InfoPanel.Tray.TrayApp(() => InfoPanelServer.LocalUrls(auth.Config.Port), auth.Config.Pin,
        app.Services.GetRequiredService<AutoStartService>(), app.Services.GetRequiredService<UpdateService>(), silent, args.Contains(UpdateService.AfterUpdateArg)));
});
tray.SetApartmentState(ApartmentState.STA);
tray.Start();
tray.Join();

await app.StopAsync();
app.Services.GetRequiredService<IHardwareMonitor>().Dispose();
