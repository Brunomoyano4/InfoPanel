using System.Text.Json;
using InfoPanel.Controls;
using InfoPanel.Platform;
using InfoPanel.Services;

namespace InfoPanel;

/// <summary>
/// El servidor de InfoPanel, igual en Windows y Linux: HTTP y HTTPS, la web, el WebSocket de telemetría y la API.
/// Cada sistema registra sus implementaciones de Platform/ con addPlatform y se ocupa de arrancarlo y de cómo
/// se muestra (bandeja en Windows, servicio de systemd en Linux).
/// </summary>
public static class InfoPanelServer
{
    public static WebApplication Build(string[] args, AuthService auth, CertificateService certs, Action<IServiceCollection> addPlatform)
    {
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
        builder.Services.AddSingleton<DashboardService>();
        builder.Services.AddSingleton<UpdateService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<UpdateService>());
        builder.Services.AddSingleton<TelemetryHub>();
        builder.Services.AddSingleton<TelemetryLoop>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<TelemetryLoop>());
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        addPlatform(builder.Services);

        var app = builder.Build();

        // La web: la carpeta wwwroot junto al ejecutable si existe (desarrollo), si no la copia embebida
        var webDir = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        Microsoft.Extensions.FileProviders.IFileProvider web = Directory.Exists(webDir)
            ? new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webDir)
            : new Microsoft.Extensions.FileProviders.ManifestEmbeddedFileProvider(typeof(InfoPanelServer).Assembly, "wwwroot");
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

        MapApi(app, auth, certs, web);
        return app;
    }

    private static IResult Result(bool ok) => ok ? Results.Ok() : Results.BadRequest();
    private static IResult ErrorResult(string? error) => error is null ? Results.Ok() : Results.BadRequest(new { error });

    private static void MapApi(WebApplication app, AuthService auth, CertificateService certs, Microsoft.Extensions.FileProviders.IFileProvider web)
    {
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

        SystemInfoDto? systemInfo = null;
        api.MapGet("/info", (IHardwareMonitor hw, ISystemMonitor sys, IAsusControl asus, ITdpControl tdp, IPlatformInfo platform) => new
        {
            id = auth.Config.InstanceId,
            machine = Environment.MachineName,
            version = UpdateService.CurrentText,
            platform = platform.Caps.Os,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            cpu = hw.CpuName,
            cores = Environment.ProcessorCount,
            asus = asus.Available,
            tdpBackend = tdp.Backend,
            // Datos fijos del equipo: se leen una vez
            system = systemInfo ??= sys.ReadInfo(),
            urls = LocalUrls(auth.Config.Port),
        });

        // Sistema
        api.MapPost("/audio", (AudioRequest r, ISystemControl c, TelemetryLoop l) => { l.InvalidateControls(); return Result(c.SetVolume(r.Volume, r.Mute)); });
        api.MapPost("/audio/output", (ActionRequest r, ISystemControl c, TelemetryLoop l) => { l.InvalidateControls(); return Result(c.SetOutput(r.Action)); });
        api.MapPost("/mic", (AudioRequest r, ISystemControl c, TelemetryLoop l) => { l.InvalidateControls(); return Result(r.Mute is { } m && c.SetMicMuted(m)); });
        api.MapPost("/brightness", (ValueRequest r, ISystemControl c, TelemetryLoop l) => { l.InvalidateControls(); return Result(c.SetBrightness(r.Value)); });
        api.MapPost("/media", (ActionRequest r, ISystemControl c) => Result(c.Media(r.Action)));
        api.MapPost("/power", (ActionRequest r, ISystemControl c) => Result(c.Power(r.Action)));
        api.MapPost("/powermode", (ActionRequest r, ISystemControl c, TelemetryLoop l) => { l.InvalidateControls(); return Result(c.SetPowerMode(r.Action)); });
        api.MapPost("/powerplan", (ActionRequest r, ISystemControl c, TelemetryLoop l) => { l.InvalidateControls(); return Result(c.SetPowerPlan(r.Action)); });
        api.MapPost("/process/kill", (ValueRequest r, ISystemControl c) => Result(c.KillProcess(r.Value)));

        // Ventiladores de placa
        api.MapPost("/fans/lhm", (LhmFanRequest r, IHardwareMonitor hw, TelemetryLoop l) => { l.InvalidateControls(); return Result(hw.SetFan(r.Id, r.Percent)); });

        // ASUS
        api.MapPost("/asus/mode", (ValueRequest r, IAsusControl a) => Result(a.SetMode(r.Value)));
        api.MapGet("/asus/curves", (int? mode, IAsusControl a) => a.GetCurves(mode));
        api.MapPost("/asus/curve", (FanCurveDto r, IAsusControl a) => ErrorResult(a.SetCurve(r)));
        api.MapPost("/asus/battery-limit", (ValueRequest r, IAsusControl a, TelemetryLoop l) => { l.InvalidateControls(); return ErrorResult(a.SetBatteryLimit(r.Value)); });

        // TDP
        api.MapPost("/tdp", (TdpRequest r, ITdpControl t, TelemetryLoop l) => { l.InvalidateControls(); return ErrorResult(t.Set(r.Spl, r.Sppt, r.Fppt)); });

        // Pantallas
        api.MapPost("/display/mode", (DisplayModeRequest r, IDisplayControl d, TelemetryLoop l) => { l.InvalidateControls(); return ErrorResult(d.SetMode(r.Device, r.Width, r.Height, r.Hz)); });
        api.MapPost("/display/active", (DisplayActiveRequest r, IDisplayControl d, TelemetryLoop l) => { l.InvalidateControls(); return ErrorResult(d.SetActive(r.Ids)); });
        api.MapPost("/display/layout", (DisplayLayoutRequest r, IDisplayControl d, TelemetryLoop l) => { l.InvalidateControls(); return ErrorResult(d.SetPositions(r.Displays, r.Primary)); });
        api.MapPost("/display/topology", (ActionRequest r, IDisplayControl d, TelemetryLoop l) => { l.InvalidateControls(); return Result(d.SetTopology(r.Action)); });
        api.MapPost("/display/preset/save", (ActionRequest r, IDisplayControl d, TelemetryLoop l) => { l.InvalidateControls(); return ErrorResult(d.SavePreset(r.Action)); });
        api.MapPost("/display/preset/apply", (ActionRequest r, IDisplayControl d, TelemetryLoop l) => { l.InvalidateControls(); return ErrorResult(d.ApplyPreset(r.Action)); });
        api.MapPost("/display/preset/delete", (ActionRequest r, IDisplayControl d, TelemetryLoop l) => { l.InvalidateControls(); return Result(d.DeletePreset(r.Action)); });

        // Dashboards personalizados
        api.MapGet("/dashboards", (DashboardService s) => Results.Content(s.GetAllJson(), "application/json"));
        api.MapPost("/dashboards/save", (System.Text.Json.Nodes.JsonObject d, DashboardService s) => ErrorResult(s.Save(d)));
        api.MapPost("/dashboards/delete", (ActionRequest r, DashboardService s) => Result(s.Delete(r.Action)));

        // Aplicaciones
        api.MapGet("/apps", (bool? refresh, IAppLauncher a) => a.GetApps(refresh == true));
        api.MapGet("/apps/icon", (string id, IAppLauncher a) => a.GetIcon(id) is { } png
            ? Results.File(png, "image/png")
            : Results.NotFound());
        api.MapPost("/apps/launch", (ActionRequest r, IAppLauncher a) => Result(a.Launch(r.Action)));

        // Inicio con el sistema
        api.MapPost("/autostart", (EnabledRequest r, IAutoStart s, TelemetryLoop l) => { l.InvalidateControls(); return ErrorResult(s.SetEnabled(r.Enabled)); });

        // Historial de versiones de esta PC (el mismo archivo de la web, por la API para que funcione con varias PCs)
        api.MapGet("/changelog", () => web.GetFileInfo("changelog.json") is { Exists: true } f
            ? Results.Stream(f.CreateReadStream(), "application/json")
            : Results.NotFound());

        // Actualizaciones
        api.MapGet("/update", (UpdateService u) => u.State);
        api.MapPost("/update/check", async (UpdateService u, TelemetryLoop l) => { var e = await u.CheckAsync(); l.InvalidateControls(); return ErrorResult(e); });
        api.MapPost("/update/install", (UpdateService u, TelemetryLoop l) => { l.InvalidateControls(); return ErrorResult(u.StartInstall()); });

        // Revocar todos los dispositivos emparejados
        api.MapPost("/unpair-all", (AuthService a) => { a.RevokeAll(); return Results.Ok(); });
    }

    /// <summary>Direcciones IPv4 de la red local por las que se puede acceder al panel.</summary>
    public static List<string> LocalUrls(int port) =>
        CertificateService.LocalIps().Select(ip => $"http://{ip}:{port}").ToList();

    public static List<string> LocalHttpsUrls(int port) =>
        CertificateService.LocalIps().Select(ip => $"https://{ip}:{port}").ToList();
}

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
