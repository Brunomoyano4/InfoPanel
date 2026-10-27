using InfoPanel;
using InfoPanel.Platform;
using InfoPanel.Services;
using QRCoder;

// Comandos para la terminal: "InfoPanel pin" muestra cómo vincular un celu (direcciones, PIN y QR)
if (args.FirstOrDefault() is "pin" or "qr")
{
    ShowPairing(new AuthService());
    return 0;
}
if (args.FirstOrDefault() is "--help" or "-h" or "help")
{
    Console.WriteLine("""
        InfoPanel: telemetría y control del equipo desde el celular.

          InfoPanel           arranca el servidor (normalmente lo hace el servicio de systemd)
          InfoPanel pin       muestra las direcciones, el PIN y un QR para vincular un celu
          InfoPanel --help    esta ayuda

        Servicio: systemctl status infopanel · journalctl -u infopanel -f
        """);
    return 0;
}

// Una sola instancia: dos procesos chocarían por el puerto
using var instance = new Mutex(true, @"Global\InfoPanel", out var isFirst);
// Recién actualizado (corriendo a mano): la versión anterior todavía está cerrándose
if (!isFirst && args.Contains(UpdateService.AfterUpdateArg))
{
    try { isFirst = instance.WaitOne(TimeSpan.FromSeconds(30)); }
    catch (AbandonedMutexException) { isFirst = true; }
}
if (!isFirst)
{
    Console.Error.WriteLine("InfoPanel ya está en ejecución (systemctl status infopanel).");
    return 1;
}

var auth = new AuthService();
var certs = new CertificateService();

var app = InfoPanelServer.Build(args, auth, certs, services =>
{
    var platform = new LinuxPlatform();
    services.AddSingleton<IPlatformInfo>(platform);
    services.AddSingleton<IUpdatePlatform>(platform);
    services.AddSingleton<IHardwareMonitor, LinuxHardwareMonitor>();
    services.AddSingleton<ISystemMonitor, LinuxSystemMonitor>();
    services.AddSingleton<ISystemControl, LinuxSystemControl>();
    services.AddSingleton<IAutoStart, LinuxAutoStart>();
    services.AddSingleton<IAsusControl, NoAsusControl>();
    services.AddSingleton<ITdpControl, NoTdpControl>();
    services.AddSingleton<IDisplayControl, NoDisplayControl>();
    services.AddSingleton<IAppLauncher, NoAppLauncher>();
});

try
{
    await app.StartAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"No se pudo iniciar InfoPanel: {ex.Message}");
    return 1;
}

if (!Sys.IsRoot)
    Console.WriteLine("Aviso: sin root no se leen el consumo del CPU ni se controlan ventiladores, energía o el inicio con el sistema.");
ShowPairing(auth, qr: false);

// Al actualizar se termina el proceso: systemd (Restart=always) arranca el ejecutable nuevo
var update = app.Services.GetRequiredService<UpdateService>();
update.RestartRequested += () => app.Lifetime.StopApplication();

await app.WaitForShutdownAsync();
app.Services.GetRequiredService<IHardwareMonitor>().Dispose();
return 0;

static void ShowPairing(AuthService auth, bool qr = true)
{
    var urls = InfoPanelServer.LocalUrls(auth.Config.Port);
    Console.WriteLine($"InfoPanel {UpdateService.CurrentText} · {Environment.MachineName}");
    Console.WriteLine(urls.Count > 0 ? string.Join(Environment.NewLine, urls.Select(u => "  " + u)) : "  Sin conexión a una red local");
    Console.WriteLine($"PIN: {auth.Config.Pin}");
    if (!qr || urls.Count == 0) return;
    // El QR abre la página de instalación, igual que el de la bandeja en Windows
    using var gen = new QRCodeGenerator();
    using var data = gen.CreateQrCode($"{urls[0]}/setup.html", QRCodeGenerator.ECCLevel.M);
    Console.WriteLine();
    Console.WriteLine(new AsciiQRCode(data).GetGraphicSmall());
}
