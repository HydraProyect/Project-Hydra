using ApexCharts;
using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Infrastructure.Autenticacion;
using CaeManager.Infrastructure.DependencyInjection;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using CaeManager.Web.Api.Integraciones;
using CaeManager.Web.Api.V1;
using CaeManager.Web.Components;
using CaeManager.Web.Components.Account;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Asignaciones;
using CaeManager.Web.Features.AsistenteIa;
using CaeManager.Web.Features.Auditoria;
using CaeManager.Web.Features.BusquedaGlobal;
using CaeManager.Web.Features.Centros;
using CaeManager.Web.Features.Clientes;
using CaeManager.Web.Features.Comunicaciones;
using CaeManager.Web.Features.Documentos;
using CaeManager.Web.Features.Empresas;
using CaeManager.Web.Features.Facturacion;
using CaeManager.Web.Features.Incidencias;
using CaeManager.Web.Features.Integraciones.Endpoints;
using CaeManager.Web.Features.Subcontratas;
using CaeManager.Web.Features.Tenants;
using CaeManager.Web.Reportes;
using CaeManager.Web.Services;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using PdfSharp.Fonts;
using Serilog;
using System.Globalization;
using System.Threading.RateLimiting;

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

// Sink de logs en la nube (Seq): inerte mientras "Serilog:Seq:ServerUrl" no
// esté configurado — mismo patrón "funciona sin configurar, se endurece con
// una variable de entorno en producción" que Sentry, KMS o Backups. Sin él,
// los logs viven solo en el volumen del contenedor y desaparecen con él, que
// es justo lo que hace indiagnosticable un incidente (P1-10 de
// docs/business/MATURITY_REVIEW.md). Seq acepta tanto una instancia propia
// como Seq cloud; la ApiKey es opcional porque una instancia sin
// autenticación no la pide.
var urlSeq = builder.Configuration["Serilog:Seq:ServerUrl"];
var apiKeySeq = builder.Configuration["Serilog:Seq:ApiKey"];

builder.Host.UseSerilog((context, services, loggerConfiguration) =>
{
    loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        // Sin esto, los eventos de varias réplicas o de varios despliegues
        // se mezclan sin poder separarse una vez en la nube.
        .Enrich.WithProperty("Aplicacion", "CaeManager")
        .Enrich.WithProperty("Entorno", context.HostingEnvironment.EnvironmentName)
        .WriteTo.Console()
        .WriteTo.File(rutaLogsAbsoluta, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 31);

    if (!string.IsNullOrWhiteSpace(urlSeq))
        loggerConfiguration.WriteTo.Seq(urlSeq, apiKey: apiKeySeq);
});

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

// OpenTelemetry (Horizonte 2.3 del plan macro): trazas de MediatR (un span
// por Command/Query, ver LoggingBehavior/Observabilidad), HTTP entrante y
// saliente (incluidas las llamadas de AsistenteIa a Anthropic/Gemini/Mistral
// OCR) y EF Core, más las 4 métricas que pide el plan (latencia por comando,
// profundidad de cola de IA, circuitos activos, documentos
// procesados/hora — ver Observabilidad.cs y ProcesadorAnalisisDocumentoHostedService)
// — todo exportado por OTLP al mismo Seq que ya recibe los logs
// correlacionados (WriteTo.Seq más arriba). Seq habla OTLP nativamente desde
// 2024.1 (trazas y métricas, endpoints /ingest/otlp/v1/*), así que esto
// reutiliza el backend ya desplegado (docker-compose.produccion.yml) en vez
// de sumar Jaeger/Prometheus/Grafana para un despliegue de un solo operador
// — la misma razón por la que el plan lo sugiere como primera opción.
// Reutiliza las dos variables del sink de logs de arriba: el mismo
// "Serilog:Seq:ApiKey" también autentica el ingest de OTLP (cabecera
// X-Seq-ApiKey, ver la documentación de Seq), y el mismo principio "inerte
// por defecto" — sin "Serilog:Seq:ServerUrl" no se registra ningún pipeline
// de OpenTelemetry, ni exportador ni instrumentación. El
// ActivitySource/Meter de Observabilidad siguen existiendo igualmente
// (LoggingBehavior y el resto los usan sin condición), pero sin listener
// StartActivity devuelve null y las métricas no tienen a quién exportar: el
// coste es marginal, no una llamada de red que reintentar en bucle.
if (!string.IsNullOrWhiteSpace(urlSeq))
{
    var otlpTracesUrl = new Uri($"{urlSeq.TrimEnd('/')}/ingest/otlp/v1/traces");
    var otlpMetricsUrl = new Uri($"{urlSeq.TrimEnd('/')}/ingest/otlp/v1/metrics");

    void ConfigurarExportadorSeq(OtlpExporterOptions opciones, Uri endpoint)
    {
        opciones.Endpoint = endpoint;
        opciones.Protocol = OtlpExportProtocol.HttpProtobuf;
        if (!string.IsNullOrWhiteSpace(apiKeySeq))
            opciones.Headers = $"X-Seq-ApiKey={apiKeySeq}";
    }

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(recurso => recurso.AddService(
            "CaeManager", serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString()))
        .WithTracing(tracing => tracing
            // MediatR: ver Observabilidad.ActivitySource / LoggingBehavior.
            .AddSource(Observabilidad.NombreOrigen)
            // HTTP entrante (páginas Razor, endpoints, API v1) y saliente
            // (Anthropic/Gemini/Mistral OCR, Microsoft Graph...).
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            // EF Core: la traza real de cada SELECT/INSERT/UPDATE — lo que
            // permite ver si una latencia alta de comando viene de la cola
            // de PuertaAccesoDatos o de la consulta misma.
            .AddEntityFrameworkCoreInstrumentation()
            .AddOtlpExporter(opciones => ConfigurarExportadorSeq(opciones, otlpTracesUrl)))
        .WithMetrics(metrics => metrics
            .AddMeter(Observabilidad.NombreOrigen)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(opciones => ConfigurarExportadorSeq(opciones, otlpMetricsUrl)));
}

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
builder.Services.AddScoped<IClienteActivoSeleccionado, CaeManager.Web.Services.ClienteActivoSeleccionado>();
builder.Services.AddScoped<CaeManager.Application.Tenants.IVistaVocabularioPreviewService, CaeManager.Web.Services.VistaVocabularioPreviewCookie>();
builder.Services.AddScoped<ITenantActual, CaeManager.Web.Services.TenantActual>();
// Scoped: cachea por circuito si la sesión es de soporte, para que registrar
// una interacción no cueste una consulta (ver TrazaSoporteService).
builder.Services.AddScoped<CaeManager.Web.Services.TrazaSoporteService>();
builder.Services.AddScoped<CaeManager.Web.Services.ActividadUsuarioService>();
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

// API pública (P3-29, docs/business/MATURITY_REVIEW.md) — todavía no
// anunciada/publicada, pero completa: esquema propio para no heredar el
// FallbackPolicy de cookie (ver policy "ApiPublica" más abajo). El tenant se
// resuelve del claim que rellena el propio handler a partir de la clave, no
// de un parámetro suelto — ver docs/MULTITENANCY.md § 8.
authenticationBuilder.AddScheme<ApiKeyAuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
    ApiKeyAuthenticationSchemeOptions.NombreEsquema, options => { });

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
    options.AccessDeniedPath = "/acceso-denegado";
});

builder.Services.AddAuthorization(options =>
{
    // Toda página/endpoint requiere sesión iniciada salvo que declare [AllowAnonymous]
    // (como Login) — ver ARCHITECTURE.md, "Autenticación y autorización".
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    // Solo el esquema ApiKey — sin esto, el FallbackPolicy (que no fija
    // esquema) aceptaría también la cookie de Identity, y un usuario con
    // sesión de navegador podría llamar a /api/v1 sin clave.
    options.AddPolicy("ApiPublica", policy => policy
        .AddAuthenticationSchemes(ApiKeyAuthenticationSchemeOptions.NombreEsquema)
        .RequireAuthenticatedUser());
});

// Rate limiting de la API pública, por tenant (no por IP — varias
// integraciones del mismo cliente comparten cupo, coherente con "por tenant"
// del ítem P3-29). Configurable sin recompilar, mismo patrón que
// RetencionDatosOptions.
var rateLimitPorMinuto = builder.Configuration.GetValue("ApiPublica:RateLimitPorMinuto", 300);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("ApiPublica", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.User.FindFirst(TenantClaimsPrincipalFactory.TipoClaimTenantId)?.Value ?? "sin-tenant",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = rateLimitPorMinuto,
            Window = TimeSpan.FromMinutes(1),
        }));
});

builder.Services.AddOpenApi("v1", options =>
{
    options.AddDocumentTransformer<ApiKeySecuritySchemeTransformer>();
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
// Techos configurables con los mismos valores de siempre por defecto: la
// suite E2E hace logins reales en serie (cada login del Administrador son
// DOS POST anónimos: credenciales + código 2FA) y supera los 10/min desde
// 127.0.0.1 — exactamente el patrón de fuerza bruta que este límite corta,
// solo que aquí es tráfico legítimo de test. El fixture E2E sube el techo
// por variable de entorno (ver WebAppFixture); en producción no hay ninguna
// configuración y aplican los valores de siempre.
var limiteCuentaAnonimo = builder.Configuration.GetValue("RateLimiting:Cuenta:LimiteAnonimo", 10);
var limiteCuentaAutenticado = builder.Configuration.GetValue("RateLimiting:Cuenta:LimiteAutenticado", 60);

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
        var limite = contexto.User.Identity?.IsAuthenticated == true ? limiteCuentaAutenticado : limiteCuentaAnonimo;

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

// CircuitOptions explícitas (ADR-008 en Project-Hydra-Negocio, Horizonte 2.1
// del plan macro) — hasta esta medición la app corría con los defaults de
// ASP.NET Core sin examinarlos. El VPS de producción (Hetzner CX22: 2 vCPU,
// ~3.7 GB, ~2.8 GB disponibles, Postgres compartiendo la misma máquina) no
// tiene margen de RAM que sobre; los defaults de .NET 10 están pensados para
// un despliegue genérico, no para este presupuesto concreto.
//
// Medido con tools/CargaCircuitos (harness de Playwright, no incluido en
// CaeManager.slnx — ver ese proyecto): hasta 160 circuitos concurrentes
// reales (login + interacción sostenida sobre /documentos) sin errores, con
// ~1.8 MB de RAM marginal por circuito — la memoria no es el recurso que se
// agota a esta escala, muy por debajo de los "10 usuarios concurrentes
// iniciales, con crecimiento moderado" de ARCHITECTURE.md. El ajuste de abajo
// no reacciona a un problema medido; es gestión preventiva de un presupuesto
// de RAM ajustado:
// PersistedCircuitInMemoryMaxRetained (novedad de .NET 10, estado persistido
// de componentes para reconexión) viene en 1000 por defecto — a ~1.8 MB por
// circuito eso es un peor caso de más de 1.5 GB solo de circuitos persistidos,
// más de la mitad del presupuesto disponible del VPS. Se acota al mismo orden
// de magnitud que el techo medido, con margen. DisconnectedCircuitMaxRetained
// se reduce a la mitad por el mismo motivo (circuitos desconectados
// acumulados por usuarios que cierran el portátil sin salir). El resto de
// valores se deja en su default documentado — no hay evidencia de esta
// medición que justifique tocarlos.
//
// Configurables (mismo patrón que RateLimiting:Cuenta:* más arriba) para
// poder ajustarlos en producción sin recompilar si la telemetría real (una
// vez haya observabilidad, ver RUNBOOK-HORIZONTE-0.md § 0.3) apunta a otro
// número.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(opcionesCircuito =>
    {
        opcionesCircuito.DisconnectedCircuitMaxRetained =
            builder.Configuration.GetValue("Circuit:DisconnectedCircuitMaxRetained", 50);
        opcionesCircuito.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(
            builder.Configuration.GetValue("Circuit:DisconnectedCircuitRetentionMinutes", 3));
        opcionesCircuito.PersistedCircuitInMemoryMaxRetained =
            builder.Configuration.GetValue("Circuit:PersistedCircuitInMemoryMaxRetained", 100);
        opcionesCircuito.MaxBufferedUnacknowledgedRenderBatches =
            builder.Configuration.GetValue("Circuit:MaxBufferedUnacknowledgedRenderBatches", 10);
    });

// "Circuitos activos" (Horizonte 2.3, ver Observabilidad.CircuitosActivos):
// singleton porque no guarda estado por circuito, solo cuenta — compone con
// las CircuitOptions de arriba, es la señal real de cuándo ese ajuste deja
// de bastar (umbral de la multi-réplica, ADR-008 § 2.1).
builder.Services.AddSingleton<CircuitHandler, CaeManager.Web.Services.MetricasCircuitHandler>();

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

// El nombre comercial se resuelve una sola vez, antes de que nada lo pinte.
// Sin configuración se queda en el histórico; ver CaeManager.Application.Common.Marca.
CaeManager.Application.Common.Marca.Configurar(builder.Configuration["Marca:Nombre"]);

var app = builder.Build();

// Modo dedicado para el paso de "pre-deploy" de Railway (ver railway.json y
// DEPLOY.md § 4 — P2 #22 de docs/business/MATURITY_REVIEW.md, una de las
// tres cosas que desbloquean multi-réplica): aplica las migraciones
// pendientes y termina, sin levantar Kestrel ni sembrar datos. Así el
// esquema se cierra una única vez, antes de que arranque ninguna réplica del
// proceso web — no N réplicas compitiendo por aplicar DDL a la vez en cada
// redeploy/reinicio.
if (args.Contains("--migrate-only"))
{
    using var scopeMigracion = app.Services.CreateScope();
    await MigrarBaseDeDatosAsync(app.Configuration, scopeMigracion.ServiceProvider);
    return;
}

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
    // Migraciones__AlArrancar=false (Railway) una vez que el
    // preDeployCommand de railway.json esté aplicándolas de verdad — hasta
    // entonces, por defecto (true), el arranque normal las aplica igual que
    // siempre: con el pre-deploy sin adoptar, es la única vía que las
    // ejecuta. Con las dos activas a la vez no hay riesgo de una sola
    // réplica (las migraciones ya aplicadas no se repiten), pero si se
    // escalase a varias réplicas simultáneas sí volvería la carrera que
    // migrate-only existe para evitar — de ahí el apagador explícito en vez
    // de dejarlo siempre encendido.
    if (app.Configuration.GetValue("Migraciones:AlArrancar", defaultValue: true))
    {
        await MigrarBaseDeDatosAsync(app.Configuration, scope.ServiceProvider);
    }

    var dbContext = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
    var userStore = scope.ServiceProvider.GetRequiredService<IUserStore<ApplicationUser>>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    // Sin sesión de usuario en el arranque no hay tenant que resolver por
    // claim — la siembra del Administrador inicial se ejecuta explícitamente
    // como tenant #1 (ver AmbitoTenantExplicito, docs/MULTITENANCY.md § 8.4).
    using (AmbitoTenantExplicito.Establecer(TenantSeedData.IdPorDefecto))
    {
        await IdentitySeeder.SeedAsync(userManager, roleManager, userStore, logger, app.Configuration, app.Environment, dbContext);
    }

    // Los datos de prueba de CAE ya no se siembran en el tenant #1: en el
    // escenario de demo de ADR-004-delegacion-consultoras-cae.md, el tenant
    // #1 juega el papel de Consultora (sin datos operativos propios, § 5.1)
    // — DelegacionDemoSeeder los siembra en un tenant Cliente Delegante
    // nuevo y establece su propio AmbitoTenantExplicito internamente.
    await DelegacionDemoSeeder.SeedAsync(dbContext, userManager, userStore, app.Configuration, logger);

    // Segundo tenant, exclusivamente para verificación E2E multi-tenant con
    // navegador real (ver PLAN-MIGRACION-MULTITENANT.md § 6) — inerte salvo
    // que SegundoTenant:Activo esté configurado explícitamente.
    await SegundoTenantSeeder.SeedAsync(dbContext, userManager, userStore, app.Configuration, logger);

    // Al final a propósito: aprovisiona la delegación de soporte —apagada—
    // de todo tenant que exista, incluidos los que acaben de sembrarse.
    // Idempotente, así que cubre también los tenants creados en arranques
    // anteriores. Aprovisionar no concede acceso: abrirlo exige motivo y
    // ventana (ver DelegacionesSoporteSeeder).
    await DelegacionesSoporteSeeder.SeedAsync(dbContext, app.Configuration, logger);
}

// Registrado antes del manejo de excepciones para envolverlo por completo:
// una petición que termina en 500 vía UseExceptionHandler se sigue
// registrando aquí con su código de estado final, no como si hubiera ido bien.
//
// EnrichDiagnosticContext corre al cerrar la petición, cuando el tenant ya
// está resuelto — por eso los servicios se piden a ctx.RequestServices y no
// por constructor: ITenantActual es scoped y este callback lo comparte un
// middleware singleton. Es lo que hace que la traza HTTP (descargas de
// documentos, exports, endpoints de identidad) salga correlacionada con el
// tenant igual que la de MediatR, que se correlaciona en LoggingBehavior.
app.UseSerilogRequestLogging(opciones =>
{
    opciones.EnrichDiagnosticContext = (contextoDiagnostico, contextoHttp) =>
    {
        var tenantActual = contextoHttp.RequestServices.GetService<ITenantActual>();
        if (tenantActual?.TenantId is { } tenantId)
            contextoDiagnostico.Set("TenantId", tenantId);

        var usuarioId = contextoHttp.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrEmpty(usuarioId))
            contextoDiagnostico.Set("UsuarioId", usuarioId);
    };
});

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
app.MapAsignacionesEndpoints();
app.MapEmpresasEndpoints();
app.MapCentrosEndpoints();
app.MapIncidenciasEndpoints();
app.MapFacturacionEndpoints();
app.MapDocumentosEndpoints();
app.MapFirmasGuardadasEndpoints();
app.MapRequisitosDocumentalesEndpoints();
app.MapComunicacionesEndpoints();
app.MapSubcontratasEndpoints();
app.MapReportesEndpoints();
app.MapAuditoriaEndpoints();
app.MapClienteActivoEndpoints();
app.MapVistaVocabularioPreviewEndpoints();
app.MapConectarMicrosoft365Endpoints();
app.MapWebhookMicrosoft365Endpoints();
app.MapWebhookWhatsAppEndpoints();

// API pública v1 (P3-29) — solo lectura, no publicada todavía. Un único
// grupo con la política de auth/rate-limit aplicada una vez, en vez de por
// endpoint: ningún MapXxxApiEndpoints necesita saber que existen.
var apiV1 = app.MapGroup("/api/v1")
    .RequireAuthorization("ApiPublica")
    .RequireRateLimiting("ApiPublica");
apiV1.MapClientesApiEndpoints();
apiV1.MapCentrosApiEndpoints();
apiV1.MapTrabajadoresApiEndpoints();
apiV1.MapDocumentosApiEndpoints();
apiV1.MapAsignacionesApiEndpoints();

// El documento OpenAPI no expone datos, solo forma — no requiere auth
// (misma práctica habitual que /swagger.json en una API privada).
app.MapOpenApi("/api/v1/openapi.json").AllowAnonymous();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// Las migraciones (DDL: CreateTable, y desde HabilitarRlsPostgres además
// ENABLE ROW LEVEL SECURITY / CREATE POLICY) exigen el rol propietario de
// las tablas — el rol de runtime que RUNBOOK-RLS.md provisiona para
// ConnectionStrings:CaeManagerDbRuntime no tiene privilegios de DDL a
// propósito (es justo lo que hace que RLS lo restrinja de verdad). Por eso
// las migraciones se aplican con una instancia propia apuntando siempre a
// CaeManagerDb (el rol propietario), sin pasar por el DbContext inyectado —
// que desde que se configura CaeManagerDbRuntime usa ese rol restringido
// para todo lo demás (ver TenantRlsConnectionInterceptor). Mientras
// CaeManagerDbRuntime no esté configurado (todos los entornos hoy) ambas
// cadenas son la misma y esto es equivalente a conectar una sola vez.
static async Task MigrarBaseDeDatosAsync(IConfiguration configuration, IServiceProvider servicios)
{
    var cadenaMigraciones = configuration.GetConnectionString("CaeManagerDb");
    var opcionesMigraciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
        .UseNpgsql(cadenaMigraciones, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
        .Options;
    await using var dbContextMigraciones = new CaeManagerDbContext(
        opcionesMigraciones,
        servicios.GetRequiredService<IDataProtectionProvider>(),
        new TenantActualAmbiental());
    await dbContextMigraciones.Database.MigrateAsync();
}
