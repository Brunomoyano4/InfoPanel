using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using InfoPanel.Controls;
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
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
// HTTP (instalación y uso simple) y HTTPS (app instalable). ListenAnyIP escucha en IPv4 e IPv6:
// sin IPv6, "localhost" en la propia PC tarda ~2 s (Windows prueba ::1 primero)
builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(auth.Config.Port);
    k.ListenAnyIP(auth.Config.HttpsPort, l => l.UseHttps(certs.Server));
});
builder.Logging.SetMinimumLevel(LogLevel.Warning);
// Al salir no espera a que se desconecten los WebSocket abiertos
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(3));

builder.Services.AddSingleton(auth);
builder.Services.AddSingleton(certs);
builder.Services.AddSingleton(json);
builder.Services.AddSingleton<DiscoveryService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DiscoveryService>());
builder.Services.AddSingleton<HardwareService>();
builder.Services.AddSingleton<SystemService>();
builder.Services.AddSingleton<SystemControl>();
builder.Services.AddSingleton<AsusAcpi>();
builder.Services.AddSingleton<AsusControl>();
builder.Services.AddSingleton<TdpControl>();
builder.Services.AddSingleton<DisplayControl>();
builder.Services.AddSingleton<AutoStartService>();
builder.Services.AddSingleton<CpuFallbackService>();
builder.Services.AddSingleton<DashboardService>();
builder.Services.AddSingleton<AppLauncher>();
builder.Services.AddSingleton<UpdateService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UpdateService>());
builder.Services.AddSingleton<TelemetryHub>();
builder.Services.AddSingleton<TelemetryLoop>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TelemetryLoop>());
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

var app = builder.Build();

// La web: la carpeta wwwroot junto al ejecutable si existe (desarrollo), si no la copia embebida en el .exe
var webDir = Path.Combine(AppContext.BaseDirectory, "wwwroot");
Microsoft.Extensions.FileProviders.IFileProvider web = Directory.Exists(webDir)
    ? new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webDir)
    : new Microsoft.Extensions.FileProviders.ManifestEmbeddedFileProvider(typeof(AuthService).Assembly, "wwwroot");
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = web });
// no-cache: el navegador revalida y recibe la web nueva después de actualizar InfoPanel
app.UseStaticFiles(new StaticFileOptions { FileProvider = web, OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache" });
app.UseWebSockets();

// CORS: la app abierta desde una PC habla directo con las demás. No hay cookies:
// cada PC exige su propio token en el encabezado Authorization, así que abrir el origen no expone nada.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        ctx.Response.Headers.AccessControlAllowOrigin = "*";
        ctx.Response.Headers.AccessControlAllowHeaders = "Authorization, Content-Type";
        ctx.Response.Headers.AccessControlAllowMethods = "GET, POST";
        if (HttpMethods.IsOptions(ctx.Request.Method))
        {
            ctx.Response.StatusCode = 204;
            return;
        }
    }
    await next();
});

// Todo /api salvo el emparejamiento y la identificación requiere token
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path;
    if (path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/pair") && !path.StartsWithSegments("/api/hello"))
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ") ? header[7..] : null;
        if (!auth.IsValid(token))
        {
            ctx.Response.StatusCode = 401;
            return;
        }
    }
    await next();
});

app.Map("/ws", async (HttpContext ctx, TelemetryHub hub) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        return;
    }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await hub.HandleAsync(ws, ctx.RequestAborted);
});

static IResult Result(bool ok) => ok ? Results.Ok() : Results.BadRequest();
static IResult ErrorResult(string? error) => error is null ? Results.Ok() : Results.BadRequest(new { error });

var api = app.MapGroup("/api");

api.MapPost("/pair", (PairRequest r) =>
    auth.Pair(r.Pin) is { } token ? Results.Ok(new { token }) : Results.Unauthorized());

// Identificación pública: permite saber si una dirección es un InfoPanel y cuál, antes de vincularse
api.MapGet("/hello", () => new
{
    app = "InfoPanel",
    id = auth.Config.InstanceId,
    name = Environment.MachineName,
    port = auth.Config.Port,
    httpsPort = auth.Config.HttpsPort,
    https = LocalHttpsUrls(auth.Config.HttpsPort),
});

// Otras PCs con InfoPanel encontradas en la red
api.MapGet("/peers", (DiscoveryService d) => d.GetPeers());

// Certificado de la CA de esta PC para instalar en el celu (es público: no contiene la clave privada)
app.MapGet("/ca.crt", () => Results.File(certs.CaDer, "application/x-x509-ca-cert", $"InfoPanel-{Environment.MachineName}.crt"));

var systemInfo = new Lazy<SystemInfoDto>(SystemService.ReadInfo);
api.MapGet("/info", (HardwareService hw, AsusControl asus, TdpControl tdp) => new
{
    id = auth.Config.InstanceId,
    machine = Environment.MachineName,
    os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    cpu = hw.CpuName,
    cores = Environment.ProcessorCount,
    asus = asus.Available,
    tdpBackend = tdp.Backend,
    system = systemInfo.Value,
    urls = LocalUrls(auth.Config.Port),
});

// Sistema
api.MapPost("/audio", (AudioRequest r, SystemControl c, TelemetryLoop l) => { l.InvalidateControls(); return Result(c.SetVolume(r.Volume, r.Mute)); });
api.MapPost("/audio/output", (ActionRequest r, SystemControl c, TelemetryLoop l) => { l.InvalidateControls(); return Result(c.SetOutput(r.Action)); });
api.MapPost("/mic", (AudioRequest r, SystemControl c, TelemetryLoop l) => { l.InvalidateControls(); return Result(r.Mute is { } m && c.SetMicMuted(m)); });
api.MapPost("/brightness", (ValueRequest r, SystemControl c, TelemetryLoop l) => { l.InvalidateControls(); return Result(c.SetBrightness(r.Value)); });
api.MapPost("/media", (ActionRequest r, SystemControl c) => Result(c.Media(r.Action)));
api.MapPost("/power", (ActionRequest r, SystemControl c) => Result(c.Power(r.Action)));
api.MapPost("/powermode", (ActionRequest r, SystemControl c, TelemetryLoop l) => { l.InvalidateControls(); return Result(c.SetPowerMode(r.Action)); });
api.MapPost("/powerplan", (ActionRequest r, SystemControl c, TelemetryLoop l) => { l.InvalidateControls(); return Result(c.SetPowerPlan(r.Action)); });
api.MapPost("/process/kill", (ValueRequest r, SystemControl c) => Result(c.KillProcess(r.Value)));

// Ventiladores de placa (LibreHardwareMonitor)
api.MapPost("/fans/lhm", (LhmFanRequest r, HardwareService hw, TelemetryLoop l) => { l.InvalidateControls(); return Result(hw.SetFan(r.Id, r.Percent)); });

// ASUS
api.MapPost("/asus/mode", (ValueRequest r, AsusControl a) => Result(a.SetMode(r.Value)));
api.MapGet("/asus/curves", (int? mode, AsusControl a) => a.GetCurves(mode));
api.MapPost("/asus/curve", (FanCurveDto r, AsusControl a) => ErrorResult(a.SetCurve(r)));
api.MapPost("/asus/battery-limit", (ValueRequest r, AsusControl a, TelemetryLoop l) => { l.InvalidateControls(); return ErrorResult(a.SetBatteryLimit(r.Value)); });

// TDP
api.MapPost("/tdp", (TdpRequest r, TdpControl t, TelemetryLoop l) => { l.InvalidateControls(); return ErrorResult(t.Set(r.Spl, r.Sppt, r.Fppt)); });

// Pantallas
api.MapPost("/display/mode", (DisplayModeRequest r, DisplayControl d, TelemetryLoop l) => { l.InvalidateControls(); return ErrorResult(d.SetMode(r.Device, r.Width, r.Height, r.Hz)); });
api.MapPost("/display/active", (DisplayActiveRequest r, DisplayControl d, TelemetryLoop l) => { l.InvalidateControls(); return ErrorResult(d.SetActive(r.Ids)); });
api.MapPost("/display/layout", (DisplayLayoutRequest r, DisplayControl d, TelemetryLoop l) => { l.InvalidateControls(); return ErrorResult(d.SetPositions(r.Displays, r.Primary)); });
api.MapPost("/display/topology", (ActionRequest r, DisplayControl d, TelemetryLoop l) => { l.InvalidateControls(); return Result(d.SetTopology(r.Action)); });
api.MapPost("/display/preset/save", (ActionRequest r, DisplayControl d, TelemetryLoop l) => { l.InvalidateControls(); return ErrorResult(d.SavePreset(r.Action)); });
api.MapPost("/display/preset/apply", (ActionRequest r, DisplayControl d, TelemetryLoop l) => { l.InvalidateControls(); return ErrorResult(d.ApplyPreset(r.Action)); });
api.MapPost("/display/preset/delete", (ActionRequest r, DisplayControl d, TelemetryLoop l) => { l.InvalidateControls(); return Result(d.DeletePreset(r.Action)); });

// Dashboards personalizados
api.MapGet("/dashboards", (DashboardService s) => Results.Content(s.GetAllJson(), "application/json"));
api.MapPost("/dashboards/save", (System.Text.Json.Nodes.JsonObject d, DashboardService s) => ErrorResult(s.Save(d)));
api.MapPost("/dashboards/delete", (ActionRequest r, DashboardService s) => Result(s.Delete(r.Action)));

// Aplicaciones
api.MapGet("/apps", (bool? refresh, AppLauncher a) => a.GetApps(refresh == true));
api.MapGet("/apps/icon", (string id, AppLauncher a) => a.GetIcon(id) is { } png
    ? Results.File(png, "image/png")
    : Results.NotFound());
api.MapPost("/apps/launch", (ActionRequest r, AppLauncher a) => Result(a.Launch(r.Action)));

// Inicio con Windows
api.MapPost("/autostart", (EnabledRequest r, AutoStartService s, TelemetryLoop l) => { l.InvalidateControls(); return ErrorResult(s.SetEnabled(r.Enabled)); });

// Actualizaciones
api.MapGet("/update", (UpdateService u) => u.State);
api.MapPost("/update/check", async (UpdateService u, TelemetryLoop l) => { var e = await u.CheckAsync(); l.InvalidateControls(); return ErrorResult(e); });
api.MapPost("/update/install", (UpdateService u, TelemetryLoop l) => { l.InvalidateControls(); return ErrorResult(u.StartInstall()); });

// Revocar todos los dispositivos emparejados
api.MapPost("/unpair-all", (AuthService a) => { a.RevokeAll(); return Results.Ok(); });

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
    System.Windows.Forms.Application.Run(new InfoPanel.Tray.TrayApp(() => LocalUrls(auth.Config.Port), auth.Config.Pin,
        app.Services.GetRequiredService<AutoStartService>(), app.Services.GetRequiredService<UpdateService>(), silent, args.Contains(UpdateService.AfterUpdateArg)));
});
tray.SetApartmentState(ApartmentState.STA);
tray.Start();
tray.Join();

await app.StopAsync();
app.Services.GetRequiredService<HardwareService>().Dispose();

record PairRequest(string Pin);
record AudioRequest(int? Volume, bool? Mute);
record ValueRequest(int Value);
record ActionRequest(string Action);
record LhmFanRequest(string Id, float? Percent);
record TdpRequest(int Spl, int Sppt, int Fppt);
record EnabledRequest(bool Enabled);
record DisplayModeRequest(string Device, int Width, int Height, int Hz);
record DisplayActiveRequest(List<string> Ids);
record DisplayLayoutRequest(List<LayoutPlacement> Displays, string Primary);

static partial class Program
{
    /// <summary>Direcciones IPv4 de la red local por las que se puede acceder al panel.</summary>
    static List<string> LocalUrls(int port) =>
        CertificateService.LocalIps().Select(ip => $"http://{ip}:{port}").ToList();

    static List<string> LocalHttpsUrls(int port) =>
        CertificateService.LocalIps().Select(ip => $"https://{ip}:{port}").ToList();
}
