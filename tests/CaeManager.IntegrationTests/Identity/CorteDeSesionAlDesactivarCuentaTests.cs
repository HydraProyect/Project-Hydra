using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.IntegrationTests.Identity;

/// <summary>
/// Desactivar una cuenta corta su sesión de verdad, contra Identity real y
/// PostgreSQL (auditoría 2026-09-20: la cookie y el circuito abiertos seguían
/// leyendo, exportando y escribiendo, y la cookie se renovaba 14 días).
///
/// <para>
/// <b>Por qué hay varios tests y no uno «desactivar → sesión muerta».</b> El
/// corte descansa en dos mecanismos independientes —el security stamp que rota
/// y el bloqueo que se comprueba— y un test de extremo a extremo pasa con
/// cualquiera de los dos: quitar la rotación del stamp lo dejaría en verde
/// gracias al bloqueo, y al revés. Cada mecanismo tiene por tanto su propio
/// test que solo él puede satisfacer:
/// </para>
/// <list type="bullet">
/// <item><see cref="Desactivar_rota_el_security_stamp"/> y
/// <see cref="Una_sesion_de_antes_no_resucita_al_reactivar_la_cuenta"/> solo los
/// satisface la rotación del stamp (al reactivar el bloqueo ya no existe).</item>
/// <item><see cref="El_validador_de_la_cookie_rechaza_una_cuenta_bloqueada_con_el_stamp_intacto"/>
/// y su gemelo del circuito solo los satisface la comprobación del bloqueo
/// (el stamp no cambia).</item>
/// </list>
/// Cada uno lleva su control positivo —la misma sesión, antes de la
/// desactivación, es aceptada— para que un rechazo no pueda ser el instrumento
/// rechazándolo todo.
/// </summary>
public class CorteDeSesionAlDesactivarCuentaTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private ServiceProvider _servicios = null!;
    private Guid _tenant;

    public async Task InitializeAsync()
    {
        var servicios = new ServiceCollection();
        servicios.AddLogging();
        servicios.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        servicios.AddSingleton<ITenantActual>(new TenantActualFijo());
        servicios.AddScoped<PuertaAccesoDatos>();
        servicios.AddHttpContextAccessor();

        servicios.AddDbContext<CaeManagerDbContext>(opciones => opciones
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL")));
        servicios.AddScoped<ITenantsQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());

        servicios.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();

        // Lo mismo que registra AddInfrastructure: la factory de claims real y
        // el SignInManager, y —lo que se prueba— la validación de sesión real,
        // registrada con la MISMA extensión que usa producción.
        servicios.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<CaeManagerDbContext>()
            .AddSignInManager<SignInManager<ApplicationUser>>()
            .AddClaimsPrincipalFactory<TenantClaimsPrincipalFactory>();
        servicios.AddValidacionDeSesionDeCuenta();

        // Intervalo del circuito: el mínimo que admite el revalidador.
        servicios.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Circuit:RevalidacionIntervaloSegundos"] = "1" })
            .Build());

        _servicios = servicios.BuildServiceProvider();

        using var ambito = _servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        await contexto.Database.MigrateAsync();

        var tenant = new Tenant("Tenant de prueba");
        contexto.Tenants.Add(tenant);
        await contexto.SaveChangesAsync();
        _tenant = tenant.Id;

        var roleManager = ambito.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var rol in Roles.Todos)
            await roleManager.CreateAsync(new IdentityRole<Guid>(rol));
    }

    public async Task DisposeAsync()
    {
        await _servicios.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    // ── Rotación del security stamp ────────────────────────────────────────

    [Fact]
    public async Task Desactivar_rota_el_security_stamp()
    {
        var id = await CrearCuentaAsync("stamp@x.test");
        var antes = await LeerAsync(id, u => u.SecurityStamp);

        await CambiarActivacionAsync(id, activar: false);

        (await LeerAsync(id, u => u.SecurityStamp)).Should().NotBe(antes,
            "desactivar tiene que invalidar las sesiones ya emitidas, y lo único que las invalida es el stamp");
    }

    [Fact]
    public async Task Reactivar_no_rota_el_stamp_de_nuevo()
    {
        // El stamp que rotó al desactivar es el que mantiene muertas las
        // sesiones viejas: reactivar no debe tocarlo (ni hace falta).
        var id = await CrearCuentaAsync("reactiva-stamp@x.test");
        await CambiarActivacionAsync(id, activar: false);
        var trasDesactivar = await LeerAsync(id, u => u.SecurityStamp);

        await CambiarActivacionAsync(id, activar: true);

        (await LeerAsync(id, u => u.SecurityStamp)).Should().Be(trasDesactivar);
    }

    [Fact]
    public async Task Una_sesion_de_antes_no_resucita_al_reactivar_la_cuenta()
    {
        // Solo lo satisface la rotación del stamp: al reactivar, el bloqueo ya
        // no existe. Con solo LockoutEnd, quitar el bloqueo devolvía la
        // validez a la cookie vieja.
        var id = await CrearCuentaAsync("no-resucita@x.test");
        var cookieDeAntes = await PrincipalAsync(id);
        (await ValidarCookieAsync(cookieDeAntes)).Should().BeTrue("control positivo: la cookie es válida antes de desactivar");

        await CambiarActivacionAsync(id, activar: false);
        await CambiarActivacionAsync(id, activar: true);

        (await ValidarCookieAsync(cookieDeAntes)).Should().BeFalse();
        (await SigueVigenteEnElCircuitoAsync(cookieDeAntes)).Should().BeFalse();
        (await ValidarCookieAsync(await PrincipalAsync(id))).Should().BeTrue(
            "control positivo: una sesión emitida DESPUÉS de reactivar sí es válida");
    }

    // ── Comprobación del bloqueo (el stamp NO cambia) ──────────────────────

    [Fact]
    public async Task El_validador_de_la_cookie_rechaza_una_cuenta_bloqueada_con_el_stamp_intacto()
    {
        var id = await CrearCuentaAsync("bloqueada-cookie@x.test");
        var cookie = await PrincipalAsync(id);
        (await ValidarCookieAsync(cookie)).Should().BeTrue("control positivo: válida antes de bloquear");
        var stampAntes = await LeerAsync(id, u => u.SecurityStamp);

        await BloquearSinRotarStampAsync(id);

        (await LeerAsync(id, u => u.SecurityStamp)).Should().Be(stampAntes, "precondición: el stamp no cambia");
        (await ValidarCookieAsync(cookie)).Should().BeFalse();
    }

    [Fact]
    public async Task El_circuito_rechaza_una_cuenta_bloqueada_con_el_stamp_intacto()
    {
        var id = await CrearCuentaAsync("bloqueada-circuito@x.test");
        var principal = await PrincipalAsync(id);
        (await SigueVigenteEnElCircuitoAsync(principal)).Should().BeTrue("control positivo: válida antes de bloquear");

        await BloquearSinRotarStampAsync(id);

        (await SigueVigenteEnElCircuitoAsync(principal)).Should().BeFalse();
    }

    [Fact]
    public async Task Un_cambio_de_stamp_sin_bloqueo_tambien_invalida()
    {
        // La otra mitad de la separación: sin bloqueo, solo el stamp corta.
        var id = await CrearCuentaAsync("solo-stamp@x.test");
        var cookie = await PrincipalAsync(id);

        using (var ambito = _servicios.CreateScope())
        {
            var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var usuario = (await userManager.FindByIdAsync(id.ToString()))!;
            await userManager.UpdateSecurityStampAsync(usuario);
        }

        (await ValidarCookieAsync(cookie)).Should().BeFalse();
        (await SigueVigenteEnElCircuitoAsync(cookie)).Should().BeFalse();
    }

    // ── De extremo a extremo: desactivar corta cookie y circuito ───────────

    [Fact]
    public async Task Desactivar_corta_la_cookie_y_el_circuito_ya_abiertos()
    {
        var id = await CrearCuentaAsync("desactivada@x.test");
        var sesion = await PrincipalAsync(id);
        (await ValidarCookieAsync(sesion)).Should().BeTrue("control positivo");

        await CambiarActivacionAsync(id, activar: false);

        (await ValidarCookieAsync(sesion)).Should().BeFalse();
        (await SigueVigenteEnElCircuitoAsync(sesion)).Should().BeFalse();
    }

    [Fact]
    public async Task El_circuito_abierto_pasa_a_anonimo_al_desactivar_la_cuenta()
    {
        // El componente real, con su temporizador real (1 s): un circuito ya
        // conectado no genera peticiones HTTP, así que solo este bucle puede
        // enterarse.
        var id = await CrearCuentaAsync("circuito-vivo@x.test");
        var principal = await PrincipalAsync(id);

        using var ambito = _servicios.CreateScope();
        var proveedor = new RevalidadorDeAutenticacionDelCircuito(
            ambito.ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>(),
            _servicios.GetRequiredService<IServiceScopeFactory>(),
            _servicios.GetRequiredService<IOptions<IdentityOptions>>(),
            _servicios.GetRequiredService<IConfiguration>());
        try
        {
            proveedor.SetAuthenticationState(Task.FromResult(new AuthenticationState(principal)));

            // Control positivo: el bucle corre (más de un ciclo) y NO expulsa a
            // una cuenta válida.
            await Task.Delay(TimeSpan.FromSeconds(2.5));
            (await proveedor.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated.Should().BeTrue();

            await CambiarActivacionAsync(id, activar: false);

            var limite = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < limite
                   && (await proveedor.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated)
                await Task.Delay(200);

            (await proveedor.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated.Should().BeFalse(
                "tras desactivar, el circuito abierto debe perder la autenticación en el siguiente ciclo");
        }
        finally
        {
            ((IDisposable)proveedor).Dispose();
        }
    }

    // ── Ayudas ─────────────────────────────────────────────────────────────

    private async Task<Guid> CrearCuentaAsync(string email)
    {
        using var ambito = _servicios.CreateScope();
        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var usuario = new ApplicationUser
        {
            UserName = email,
            Email = email,
            NombreCompleto = email,
            TenantId = _tenant,
            LockoutEnabled = true,
        };
        (await userManager.CreateAsync(usuario, "Contrasena#Segura2026")).Succeeded.Should().BeTrue();
        (await userManager.AddToRoleAsync(usuario, Roles.GestorCae)).Succeeded.Should().BeTrue();
        return usuario.Id;
    }

    /// <summary>La misma escritura que hace la pantalla <c>/usuarios</c>.</summary>
    private async Task CambiarActivacionAsync(Guid id, bool activar)
    {
        using var ambito = _servicios.CreateScope();
        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var usuario = (await userManager.FindByIdAsync(id.ToString()))!;
        (await SesionDeCuenta.CambiarActivacionAsync(userManager, usuario, activar)).Succeeded.Should().BeTrue();
    }

    /// <summary>Bloqueo por cualquier vía que NO rote el stamp.</summary>
    private async Task BloquearSinRotarStampAsync(Guid id)
    {
        using var ambito = _servicios.CreateScope();
        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var usuario = (await userManager.FindByIdAsync(id.ToString()))!;
        (await userManager.SetLockoutEndDateAsync(usuario, DateTimeOffset.MaxValue)).Succeeded.Should().BeTrue();
    }

    private async Task<T> LeerAsync<T>(Guid id, Func<ApplicationUser, T> campo)
    {
        using var ambito = _servicios.CreateScope();
        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return campo((await userManager.FindByIdAsync(id.ToString()))!);
    }

    /// <summary>El principal que llevaría la cookie, emitido por la factory real.</summary>
    private async Task<ClaimsPrincipal> PrincipalAsync(Guid id)
    {
        using var ambito = _servicios.CreateScope();
        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var fabrica = ambito.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();
        var usuario = (await userManager.FindByIdAsync(id.ToString()))!;
        return await fabrica.CreateAsync(usuario);
    }

    /// <summary>
    /// Pasa el principal por el <c>ISecurityStampValidator</c> REAL con la
    /// cookie «vencida» (emitida hace una hora, más que el intervalo de
    /// revalidación), como haría el middleware de cookies. Devuelve si la
    /// cookie sigue aceptada.
    /// </summary>
    private async Task<bool> ValidarCookieAsync(ClaimsPrincipal principal)
    {
        using var ambito = _servicios.CreateScope();
        var http = new DefaultHttpContext { RequestServices = ambito.ServiceProvider };
        ambito.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;

        var opciones = ambito.ServiceProvider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var propiedades = new AuthenticationProperties
        {
            IssuedUtc = DateTimeOffset.UtcNow.AddHours(-1),
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(13),
        };
        var ticket = new AuthenticationTicket(principal, propiedades, IdentityConstants.ApplicationScheme);
        var contexto = new CookieValidatePrincipalContext(
            http,
            new AuthenticationScheme(IdentityConstants.ApplicationScheme, null, typeof(CookieAuthenticationHandler)),
            opciones,
            ticket);

        await ambito.ServiceProvider.GetRequiredService<ISecurityStampValidator>().ValidateAsync(contexto);

        return contexto.Principal is not null;
    }

    private async Task<bool> SigueVigenteEnElCircuitoAsync(ClaimsPrincipal principal)
    {
        using var ambito = _servicios.CreateScope();
        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return await SesionDeCuenta.SigueVigenteAsync(
            userManager, ambito.ServiceProvider.GetRequiredService<IOptions<IdentityOptions>>().Value, principal);
    }

    private sealed class TenantActualFijo : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }
}
