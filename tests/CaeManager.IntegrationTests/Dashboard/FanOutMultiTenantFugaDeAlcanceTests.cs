using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests.Dashboard;

/// <summary>
/// Reproduce el DEFECTO 1 del fan-out multi-tenant (hallazgo Codex
/// 2026-09-11, visto primero en <c>ObtenerKpisGlobalesQuery</c> y con el mismo
/// mecanismo en <c>ObtenerDashboardEjecutivoQuery</c>): ambas Queries
/// reutilizan la MISMA instancia de <see cref="AlcanceDatosService"/> y
/// <see cref="CurrentUserService"/> (scoped, una por petición) para varios
/// Clientes Delegantes, cambiando solo <see cref="AmbitoTenantExplicito"/> en
/// cada vuelta del bucle. Dos causas que se sumaban:
///
/// <list type="bullet">
/// <item>(a) <c>AlcanceDatosService</c> memoizaba acceso total/cartera en un
/// único valor por instancia: el del PRIMER tenant visitado se servía, sin
/// volver a resolverse, a cada tenant siguiente.</item>
/// <item>(b) <c>CurrentUserService.ObtenerRolActualAsync</c> ignoraba
/// <c>AmbitoTenantExplicito</c> y devolvía siempre el rol de la sesión o el
/// del workspace seleccionado en la UI —ninguno de los dos es el rol
/// efectivo en el Cliente Delegante que el bucle está visitando.</item>
/// </list>
///
/// Cada test de este fichero ejercita el servicio REAL (no un doble), porque
/// el defecto vive en la implementación de Infrastructure/Web, no en un
/// contrato que un Falso pudiera dejar pasar igual (mismo criterio que
/// <c>AlcanceDatosServiceSobreRelacionEmpresarialTests</c>).
/// </summary>
public class FanOutMultiTenantFugaDeAlcanceTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantConsultora = Guid.NewGuid();
    private readonly Guid _tenantDelegante = Guid.NewGuid();
    private readonly Guid _tenantDeleganteSinCartera = Guid.NewGuid();
    private readonly Guid _usuario = Guid.NewGuid();
    private Guid _clienteDentroCartera;
    private Guid _clienteFueraCartera;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        // DelegacionTenant/AsignacionOperadorDelegado son catálogo global sin
        // filtro (ver RolEfectivoEnDelegacionTests) — el mismo usuario es
        // GestorCae en los dos Clientes Delegantes, Administrador en su
        // propia Consultora (vía el claim de sesión, no una fila aquí).
        var delegacionConCartera = new DelegacionTenant(_tenantConsultora, _tenantDelegante);
        var delegacionSinCartera = new DelegacionTenant(_tenantConsultora, _tenantDeleganteSinCartera);
        contexto.DelegacionesTenant.AddRange(delegacionConCartera, delegacionSinCartera);
        contexto.AsignacionesOperadorDelegado.AddRange(
            new AsignacionOperadorDelegado(delegacionConCartera.Id, _usuario, Roles.GestorCae),
            new AsignacionOperadorDelegado(delegacionSinCartera.Id, _usuario, Roles.GestorCae));
        await contexto.SaveChangesAsync();

        var ahora = DateTime.UtcNow;
        using (AmbitoTenantExplicito.Establecer(_tenantDelegante))
        {
            var clienteDentro = Empresa.CrearComoCliente("Dentro de cartera S.A.", "B10380186", false, null, null);
            var clienteFuera = Empresa.CrearComoCliente("Fuera de cartera S.A.", "B10380194", false, null, null);
            contexto.Empresas.AddRange(clienteDentro, clienteFuera);
            await contexto.SaveChangesAsync();
            _clienteDentroCartera = clienteDentro.Id;
            _clienteFueraCartera = clienteFuera.Id;

            // La Consultora opera Outbound completo sobre el Delegante (ámbito
            // universal de la OPERACIÓN, el caso habitual de una delegación
            // comercial completa), pero la CARTERA del usuario dentro de esa
            // operación está acotada a un único Cliente — el escenario exacto
            // del enunciado del defecto.
            var operacion = AsignacionOperacion.Externa(
                _tenantDelegante, _tenantConsultora, ServicioCae.Outbound,
                AmbitoAsignacion.Universal, ahora, null, ahora);
            contexto.AsignacionesOperacion.Add(operacion);
            contexto.AsignacionesCartera.Add(AsignacionCartera.Externa(
                operacion, _usuario, Roles.GestorCae,
                AmbitoAsignacion.DeRelacionCliente(_clienteDentroCartera), ahora, null, ahora));
            await contexto.SaveChangesAsync();
        }

        // El segundo Delegante deliberadamente NO tiene ninguna
        // AsignacionOperacion/AsignacionCartera: GestorCae ahí, pero sin
        // ninguna cartera — alcance cero, no acceso total (escenario 3 del
        // encargo).
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    /// <summary>
    /// Reproducción directa del DEFECTO 1: procesa primero el propio tenant
    /// (Administrador, acceso total) y después el Delegante con cartera
    /// acotada, con las MISMAS instancias de <see cref="AlcanceDatosService"/>
    /// y <see cref="CurrentUserService"/> — igual que
    /// <c>ObtenerKpisGlobalesQueryHandler</c> reutiliza su ámbito de DI para
    /// cada Cliente autorizado. Antes del fix: el Delegante heredaba el
    /// acceso total cacheado del primer tenant y veía también
    /// <c>_clienteFueraCartera</c>.
    /// </summary>
    [Fact]
    public async Task Un_administrador_de_la_consultora_no_hereda_acceso_total_al_visitar_un_delegante_con_cartera_acotada()
    {
        await using var contexto = CrearContexto();
        var currentUserService = CrearCurrentUserService(contexto);
        var alcanceDatos = new AlcanceDatosService(
            contexto, currentUserService, new TenantActualPorAmbito(), new SesionPrivilegiadaAusente());

        IReadOnlyList<Guid>? clienteIdsPropio;
        using (AmbitoTenantExplicito.Establecer(_tenantConsultora))
            clienteIdsPropio = await alcanceDatos.ObtenerClienteIdsVisiblesAsync();

        clienteIdsPropio.Should().BeNull("Administrador en su propio tenant tiene acceso total");

        IReadOnlyList<Guid>? clienteIdsDelegado;
        using (AmbitoTenantExplicito.Establecer(_tenantDelegante))
            clienteIdsDelegado = await alcanceDatos.ObtenerClienteIdsVisiblesAsync();

        clienteIdsDelegado.Should().NotBeNull(
            "el rol efectivo ahí es GestorCae con cartera, nunca acceso total heredado del tenant anterior");
        clienteIdsDelegado.Should().BeEquivalentTo([_clienteDentroCartera]);
        clienteIdsDelegado.Should().NotContain(_clienteFueraCartera);
    }

    /// <summary>
    /// Misma reproducción en orden inverso: primero el Delegante SIN
    /// ninguna cartera (alcance cero), después el propio tenant. Antes del
    /// fix, el caché de instancia también podía servir en la otra dirección
    /// —o simplemente nunca resolver el rol correcto porque
    /// <c>ObtenerRolActualAsync</c> ignoraba el ámbito— así que se comprueba
    /// que ninguna de las dos filas contamina a la otra en ningún sentido.
    /// </summary>
    [Fact]
    public async Task El_orden_de_visita_no_cambia_el_alcance_resuelto_para_cada_tenant()
    {
        await using var contexto = CrearContexto();
        var currentUserService = CrearCurrentUserService(contexto);
        var alcanceDatos = new AlcanceDatosService(
            contexto, currentUserService, new TenantActualPorAmbito(), new SesionPrivilegiadaAusente());

        IReadOnlyList<Guid>? clienteIdsSinCartera;
        using (AmbitoTenantExplicito.Establecer(_tenantDeleganteSinCartera))
            clienteIdsSinCartera = await alcanceDatos.ObtenerClienteIdsVisiblesAsync();

        clienteIdsSinCartera.Should().NotBeNull().And.BeEmpty(
            "GestorCae sin ninguna Asignación de Cartera ahí: alcance cero, nunca null (acceso total) ni la cartera de otro tenant");

        IReadOnlyList<Guid>? clienteIdsPropio;
        using (AmbitoTenantExplicito.Establecer(_tenantConsultora))
            clienteIdsPropio = await alcanceDatos.ObtenerClienteIdsVisiblesAsync();

        clienteIdsPropio.Should().BeNull("el tenant propio sigue dando acceso total aunque se visite después de uno sin cartera");
    }

    /// <summary>
    /// Aísla la causa (b) del rol: sin ningún workspace seleccionado en la UI
    /// (el caso real de la Visión de cartera, que agrega FUERA de cualquier
    /// workspace), el rol efectivo tiene que depender del tenant que marca
    /// <see cref="AmbitoTenantExplicito"/>, no quedarse fijo en el de la
    /// sesión.
    /// </summary>
    [Fact]
    public async Task El_rol_efectivo_cambia_con_el_ambito_explicito_sin_ningun_workspace_seleccionado()
    {
        await using var contexto = CrearContexto();
        var currentUserService = CrearCurrentUserService(contexto);

        using (AmbitoTenantExplicito.Establecer(_tenantConsultora))
            (await currentUserService.ObtenerRolActualAsync()).Should().Be(
                "Administrador", "es el propio tenant de origen: el rol es el del claim de sesión");

        using (AmbitoTenantExplicito.Establecer(_tenantDelegante))
            (await currentUserService.ObtenerRolActualAsync()).Should().Be(
                Roles.GestorCae, "es el rol de la AsignacionOperadorDelegado en ESE Delegante, no el de la sesión");

        using (AmbitoTenantExplicito.Establecer(_tenantDeleganteSinCartera))
            (await currentUserService.ObtenerRolActualAsync()).Should().Be(
                Roles.GestorCae, "GestorCae ahí también, aunque no tenga ninguna cartera bajo ese rol");
    }

    private CurrentUserService CrearCurrentUserService(ITenantsQueryContext contexto)
    {
        var identidad = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, _usuario.ToString()),
                new Claim(ClaimTypes.Role, "Administrador"),
                new Claim(TenantClaimsPrincipalFactory.TipoClaimTenantId, _tenantConsultora.ToString())
            ],
            "prueba");

        var servicios = new ServiceCollection();
        servicios.AddSingleton(contexto);

        return new CurrentUserService(
            new AuthenticationStateProviderFalso(new ClaimsPrincipal(identidad)),
            new HttpContextAccessorFalso(),
            new ClienteActivoSeleccionadoFalso(),
            servicios.BuildServiceProvider());
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualPorAmbito();
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    /// <summary>Mismo <see cref="ITenantActual"/> puramente ambiental que usa DashboardEjecutivoMultiTenantTests para el fan-out.</summary>
    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    /// <summary>Sin ningún workspace seleccionado: el caso real de la Visión de cartera, que agrega fuera de cualquier Delegated Workspace.</summary>
    private sealed class ClienteActivoSeleccionadoFalso : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private sealed class AuthenticationStateProviderFalso(ClaimsPrincipal usuario) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(usuario));
    }

    private sealed class HttpContextAccessorFalso : IHttpContextAccessor
    {
        public HttpContext? HttpContext
        {
            get => null;
            set => throw new NotSupportedException();
        }
    }
}
