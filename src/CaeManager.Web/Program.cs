using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Infrastructure.DependencyInjection;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using CaeManager.Web.Components;
using CaeManager.Web.Components.Account;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.AsistenteIa;
using CaeManager.Web.Features.Auditoria;
using CaeManager.Web.Features.BusquedaGlobal;
using CaeManager.Web.Features.Clientes;
using CaeManager.Web.Features.Documentos;
using CaeManager.Web.Reportes;
using CaeManager.Web.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using PdfSharp.Fonts;
using Serilog;
using System.Globalization;

// El resolver de fuentes de PDFsharp 6 es global e independiente del ciclo de
// vida de DI — se registra una sola vez al arrancar (ver EmbeddedFontResolver).
GlobalFontSettings.FontResolver = new EmbeddedFontResolver();

var builder = WebApplication.CreateBuilder(args);

// Todo el producto es en español (ver UX_PATTERNS.md) — fechas y números se
// formatean con la cultura es-ES en toda la aplicación, no por pantalla.
var culturaEspanola = new CultureInfo("es-ES");
CultureInfo.DefaultThreadCurrentCulture = culturaEspanola;
CultureInfo.DefaultThreadCurrentUICulture = culturaEspanola;

// Logging estructurado con Serilog — sustituye al proveedor de logging por
// defecto de Microsoft.Extensions.Logging (la sección "Logging" del
// appsettings ya no se usa; los niveles ahora se leen de "Serilog", con el
// mismo mecanismo de env vars que el resto de la app, p. ej.
// Serilog__MinimumLevel__Default=Warning). Los sitios que ya hacen
// logger.LogInformation/LogWarning/LogError (IdentitySeeder,
// DatosPruebaSeeder) no necesitan cambios: solo se sustituye el proveedor,
// no la API de ILogger.
//
// La ruta del sink de archivo sigue el mismo patrón que
// DataProtection:RutaClaves / AlmacenamientoArchivos:Ruta (relativa al
// content root si no es absoluta) en vez de vivir dentro del JSON de
// Serilog, para poder fijarla con una única variable de entorno con el
// mismo estilo de clave en español que el resto de esta app.
var rutaLogs = builder.Configuration["Logging:RutaArchivo"] ?? "App_Data/logs/log-.txt";
var rutaLogsAbsoluta = Path.IsPathRooted(rutaLogs)
    ? rutaLogs
    : Path.Combine(builder.Environment.ContentRootPath, rutaLogs);

builder.Host.UseSerilog((context, services, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(rutaLogsAbsoluta, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 31));

// Para añadir un sink en la nube (Seq, Axiom, etc.) más adelante: agregar el
// paquete NuGet correspondiente y un .WriteTo.Xxx(...) condicionado a que su
// clave de configuración (p. ej. "Serilog:Seq:ServerUrl") esté presente en
// builder.Configuration — mismo patrón "funciona sin configurar, se
// endurece con una variable de entorno en producción" que
// DataProtection/AlmacenamientoArchivos. Ningún paquete de sink en la nube
// está referenciado todavía porque no hay cuenta provisionada (ver
// RUNBOOK-CLAVES.md / ROADMAP.md).

// Error tracking con Sentry — si "Sentry:Dsn" no está configurado (hoy, en
// todos los entornos: no hay cuenta de Sentry provisionada todavía), la SDK
// queda inerte por diseño propio: no envía nada, no lanza, no bloquea el
// arranque. IMPORTANTE: hay que pasar explícitamente "" (no null) para que
// quede inerte — un Dsn null hace que Sentry.SentrySdk.InitHub lance
// ArgumentNullException en el arranque en vez de desactivarse en silencio
// (comprobado en local, no es el comportamiento que sugiere la documentación
// a primera vista). El middleware de Sentry se registra internamente vía
// IStartupFilter y envuelve TODO el pipeline HTTP, incluido
// app.UseExceptionHandler("/Error", ...) más abajo — captura la excepción
// real para reportarla y la deja seguir su curso normal hacia la página de
// error genérica ya existente (ver ARCHITECTURE.md, "Excepciones reservadas
// para errores verdaderamente inesperados").
builder.WebHost.UseSentry(options =>
{
    options.Dsn = builder.Configuration["Sentry:Dsn"] ?? string.Empty;
    options.Environment = builder.Environment.EnvironmentName;
    options.SendDefaultPii = false;
});

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
builder.Services.AddScoped<ToastService>();
builder.Services.AddScoped<BusquedaGlobalService>();
builder.Services.AddScoped<AsistenteIaService>();

builder.Services.AddCascadingAuthenticationState();
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/cuenta/iniciar-sesion";
    options.AccessDeniedPath = "/cuenta/iniciar-sesion";
});

builder.Services.AddAuthorization(options =>
{
    // Toda página/endpoint requiere sesión iniciada salvo que declare [AllowAnonymous]
    // (como Login) — ver ARCHITECTURE.md, "Autenticación y autorización".
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Detrás de un proxy inverso (Railway, cualquier despliegue en contenedor —
// ver DEPLOY.md), Kestrel solo ve tráfico HTTP interno; sin esto,
// UseHttpsRedirection/UseHsts no reconocen la petición original como HTTPS
// y pueden entrar en bucle de redirección. KnownProxies/KnownNetworks se
// dejan vacíos a propósito: el proxy de entrada cambia según dónde se
// despliegue, y este es un único servicio detrás de un solo proxy de borde,
// no una red interna con saltos que haya que enumerar.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    KnownProxies = { },
    KnownIPNetworks = { }
});

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
    await dbContext.Database.MigrateAsync();

    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await IdentitySeeder.SeedAsync(userManager, roleManager, logger, app.Configuration);
    await DatosPruebaSeeder.SeedAsync(dbContext, userManager, app.Configuration, logger);
}

// Registrado antes del manejo de excepciones para envolverlo por completo:
// una petición que termina en 500 vía UseExceptionHandler se sigue
// registrando aquí con su código de estado final, no como si hubiera ido bien.
app.UseSerilogRequestLogging();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseRequestLocalization(new RequestLocalizationOptions()
    .SetDefaultCulture(culturaEspanola.Name)
    .AddSupportedCultures(culturaEspanola.Name)
    .AddSupportedUICultures(culturaEspanola.Name));

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// Los archivos estáticos (JS/CSS) no son sensibles y nunca deben exigir
// sesión iniciada — dejarlos detrás de la FallbackPolicy generaba una
// carrera real: en una navegación fresca, blazor.web.js y nuestros propios
// módulos JS a veces se pedían antes de que la cookie de auth completara su
// ida y vuelta, y un import() dinámico fallido no se reintenta solo.
app.MapStaticAssets().AllowAnonymous();
app.MapGet("/salud", () => Results.Ok("ok")).AllowAnonymous();
app.MapIdentityEndpoints();
app.MapClientesEndpoints();
app.MapDocumentosEndpoints();
app.MapReportesEndpoints();
app.MapAuditoriaEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
