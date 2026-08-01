using ApexCharts;
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
using CaeManager.Web.Features.Tenants;
using CaeManager.Web.Reportes;
using CaeManager.Web.Services;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
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
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
builder.Services.AddScoped<IClienteActivoSeleccionado, CaeManager.Web.Services.ClienteActivoSeleccionado>();
builder.Services.AddScoped<ITenantActual, CaeManager.Web.Services.TenantActual>();
// Scoped: cachea por circuito si la sesión es de soporte, para que registrar
// una interacción no cueste una consulta (ver TrazaSoporteService).
builder.Services.AddScoped<CaeManager.Web.Services.TrazaSoporteService>();
builder.Services.AddScoped<ToastService>();
builder.Services.AddScoped<BusquedaGlobalService>();
builder.Services.AddScoped<AsistenteIaService>();
builder.Services.AddScoped<CaeManager.Web.Components.Workspace.ContextWorkspaceService>();
builder.Services.AddApexCharts();

builder.Services.AddCascadingAuthenticationState();
var authenticationBuilder = builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    });
authenticationBuilder.AddIdentityCookies();

// Login corporativo vía Microsoft Entra ID (SSO), opcional — ver
// AzureAdOptions y RestriccionLoginLocalClaimsTransformation. Sin las tres
// variables configuradas, este proveedor externo ni se registra: el login
// local sigue siendo el único camino y se comporta exactamente igual que
// hoy (mismo principio "inerte por defecto" que Sentry/Backups/Anthropic).
var azureAd = builder.Configuration.GetSection(AzureAdOptions.SeccionConfiguracion).Get<AzureAdOptions>() ?? new AzureAdOptions();
if (azureAd.EstaConfigurado)
{
    authenticationBuilder.AddOpenIdConnect(IdentityEndpointsExtensions.EsquemaMicrosoft, "Microsoft (empresa)", options =>
    {
        options.Authority = $"{azureAd.Instance}{azureAd.TenantId}/v2.0";
        options.ClientId = azureAd.ClientId;
        options.ClientSecret = azureAd.ClientSecret;
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.CallbackPath = "/signin-microsoft";
        options.SignInScheme = IdentityConstants.ExternalScheme;
        options.SaveTokens = false;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
    });
}

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

// Rate limiting por IP sobre los POST de autenticación en /cuenta/* (login
// local, callback de Microsoft, verificación 2FA) — junto con el lockout de
// Identity, cierra el hallazgo P0-2 de docs/business/MATURITY_REVIEW.md
// (fuerza bruta sin fricción): el lockout protege cada cuenta concreta, este
// límite frena el barrido de muchas cuentas distintas desde una misma IP.
// Solo se limitan los POST — un GET a /cuenta/iniciar-sesion es simplemente
// cargar la página, y limitarlo también castigaba tráfico legítimo: un
// runner de CI (o un usuario real detrás de un NAT/proxy compartido) hace
// varias cargas de página desde la misma IP y se quedaba sin poder ni ver el
// formulario de login (regresión real, encontrada en los E2E de este mismo
// cambio — devolvía 429 antes de que existiera nada que enviar). El resto de
// la aplicación no se limita: es Blazor Server con sesión iniciada, el
// tráfico útil viaja por el circuito SignalR, no por peticiones HTTP
// repetidas. Limitador en memoria: suficiente mientras el techo sea 1
// réplica (autodocumentado en ARCHITECTURE.md); con multi-réplica habría que
// moverlo a un almacén compartido, igual que el resto de estado de proceso.
builder.Services.AddRateLimiter(opciones =>
{
    opciones.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    opciones.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter.Create<HttpContext, string>(contexto =>
    {
        var esPostDeCuenta = HttpMethods.IsPost(contexto.Request.Method)
            && contexto.Request.Path.StartsWithSegments("/cuenta");

        if (!esPostDeCuenta)
            return System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("sin-limite");

        // Con sesión iniciada (cambio de Delegated Workspace vía
        // /cuenta/cliente-activo, cerrar sesión) el margen es holgado — el
        // objetivo son los anónimos que martillean el login.
        var limite = contexto.User.Identity?.IsAuthenticated == true ? 60 : 10;

        // La IP real ya está resuelta: UseForwardedHeaders corre al principio
        // del pipeline (ver más abajo) y el middleware de rate limiting actúa
        // después, por petición.
        var ip = contexto.Connection.RemoteIpAddress?.ToString() ?? "desconocida";

        return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            $"{limite}:{ip}",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = limite,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
    });
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Health check real (P0-5 de docs/business/MATURITY_REVIEW.md): /salud
// respondía "ok" incondicional — con PostgreSQL caído seguía dando 200 y
// cualquier uptime check externo veía un servicio sano que no podía servir
// ni el login. Ahora ejecuta un SELECT 1 contra la base de datos: 200
// "Healthy" solo si el proceso vive Y la BD responde. Sigue siendo anónimo
// y barato a propósito (es lo que sondea Railway y el uptime check externo).
builder.Services.AddHealthChecks()
    .AddNpgSql(
        sp => builder.Configuration.GetConnectionString("CaeManagerDb")
            ?? throw new InvalidOperationException("Falta el connection string CaeManagerDb."),
        name: "postgresql");

var app = builder.Build();

// Detrás de un proxy inverso (Railway, cualquier despliegue en contenedor —
// ver DEPLOY.md), Kestrel solo ve tráfico HTTP interno; sin esto,
// UseHttpsRedirection/UseHsts no reconocen la petición original como HTTPS
// y pueden entrar en bucle de redirección. KnownProxies/KnownNetworks se
// dejan vacíos a propósito: el proxy de entrada cambia según dónde se
// despliegue, y este es un único servicio detrás de un solo proxy de borde,
// no una red interna con saltos que haya que enumerar.
var opcionesForwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
// KnownProxies/KnownIPNetworks traen loopback por defecto: un inicializador
// `= { }` no los vacía, solo no añade nada más. Sin este Clear() explícito,
// el middleware descarta X-Forwarded-Proto porque el edge de Railway no es
// loopback, y la app cree que la petición es HTTP (genera Location: http://
// en redirects, lo que rompe el login vía CSP form-action).
opcionesForwardedHeaders.KnownProxies.Clear();
opcionesForwardedHeaders.KnownIPNetworks.Clear();
app.UseForwardedHeaders(opcionesForwardedHeaders);

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
    await dbContext.Database.MigrateAsync();

    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    // Sin sesión de usuario en el arranque no hay tenant que resolver por
    // claim — la siembra del Administrador inicial se ejecuta explícitamente
    // como tenant #1 (ver AmbitoTenantExplicito, docs/MULTITENANCY.md § 8.4).
    using (AmbitoTenantExplicito.Establecer(TenantSeedData.IdPorDefecto))
    {
        await IdentitySeeder.SeedAsync(userManager, roleManager, logger, app.Configuration, app.Environment);
    }

    // Los datos de prueba de CAE ya no se siembran en el tenant #1: en el
    // escenario de demo de ADR-004-delegacion-consultoras-cae.md, el tenant
    // #1 juega el papel de Consultora (sin datos operativos propios, § 5.1)
    // — DelegacionDemoSeeder los siembra en un tenant Cliente Delegante
    // nuevo y establece su propio AmbitoTenantExplicito internamente.
    await DelegacionDemoSeeder.SeedAsync(dbContext, userManager, app.Configuration, logger);

    // Segundo tenant, exclusivamente para verificación E2E multi-tenant con
    // navegador real (ver PLAN-MIGRACION-MULTITENANT.md § 6) — inerte salvo
    // que SegundoTenant:Activo esté configurado explícitamente.
    await SegundoTenantSeeder.SeedAsync(dbContext, userManager, app.Configuration, logger);

    // Al final a propósito: aprovisiona la delegación de soporte —apagada—
    // de todo tenant que exista, incluidos los que acaben de sembrarse.
    // Idempotente, así que cubre también los tenants creados en arranques
    // anteriores. Aprovisionar no concede acceso: abrirlo exige motivo y
    // ventana (ver DelegacionesSoporteSeeder).
    await DelegacionesSoporteSeeder.SeedAsync(dbContext, logger);
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

// Antes de todo lo que produce respuesta (páginas, endpoints y estáticos):
// las cabeceras han de ir en cualquier respuesta, incluidas las de error.
app.UseCabecerasSeguridad();

app.UseRequestLocalization(new RequestLocalizationOptions()
    .SetDefaultCulture(culturaEspanola.Name)
    .AddSupportedCultures(culturaEspanola.Name)
    .AddSupportedUICultures(culturaEspanola.Name));

app.UseAuthentication();

// Tras UseAuthentication (el límite distingue anónimo/autenticado) y antes
// de que ningún endpoint procese la petición.
app.UseRateLimiter();

app.UseAuthorization();
app.UseAntiforgery();

// Después de UseAuthentication (hace falta el usuario resuelto) y antes de
// que nada resuelva el tenant.
app.UseRevalidacionClienteActivo();

// Los archivos estáticos (JS/CSS) no son sensibles y nunca deben exigir
// sesión iniciada — dejarlos detrás de la FallbackPolicy generaba una
// carrera real: en una navegación fresca, blazor.web.js y nuestros propios
// módulos JS a veces se pedían antes de que la cookie de auth completara su
// ida y vuelta, y un import() dinámico fallido no se reintenta solo.
app.MapStaticAssets().AllowAnonymous();
app.MapHealthChecks("/salud").AllowAnonymous();
app.MapIdentityEndpoints();
app.MapClientesEndpoints();
app.MapDocumentosEndpoints();
app.MapReportesEndpoints();
app.MapAuditoriaEndpoints();
app.MapClienteActivoEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
