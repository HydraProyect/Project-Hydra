using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Infrastructure.DependencyInjection;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using CaeManager.Web.Components;
using CaeManager.Web.Components.Account;
using CaeManager.Web.Components.DesignSystem;
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

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
builder.Services.AddScoped<ToastService>();
builder.Services.AddScoped<BusquedaGlobalService>();

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
