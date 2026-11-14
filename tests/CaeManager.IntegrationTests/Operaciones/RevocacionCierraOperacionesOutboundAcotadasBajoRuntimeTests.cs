using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Commands.CrearDelegacionTenant;
using CaeManager.Application.Tenants.Commands.DesactivarDelegacionTenant;
using CaeManager.Application.Tenants.Commands.ReactivarDelegacionTenant;
using CaeManager.Application.Tenants.Commands.RevocarAsignacionOperadorDelegado;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Operaciones;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests.Operaciones;

/// <summary>
/// Decisión de Chris del 2026-11-14 (hallazgo de Codex en la revisión de la PR #2215): revocar la
/// delegación de un Operador CAE externo, o retirar a una persona de su equipo, cierra toda la
/// autorización Outbound que deriva de ella sobre el Tenant propietario: las Asignaciones de
/// Operación del Tenant entero y también las acotadas a un Cliente empresarial, con sus
/// Asignaciones de Cartera. Antes solo se cerraba la operación vigente del Tenant entero, y una
/// acotada con su cartera seguía autorizando por Operación
/// (<c>TenantsBeneficiariosAutorizados.CarterasPorOperacionDeLaOrganizacion</c> no mira el ámbito).
///
/// <para>
/// Contra Postgres real con el cableado de producción del <see cref="ArnesDeArranqueRuntime"/>
/// (<c>cae_app_runtime</c>, interceptores, RLS sin debilitar) y con Identity real. Cada persona actúa
/// desde su propio Workspace activo, como en producción. El propietario de la base solo siembra lo
/// que ningún comando de hoy escribe (las operaciones acotadas) y lee el resultado. La vía de
/// Soporte TALVEG se mide en <c>SoporteRevocaOperadorCaeExternoBajoRuntimeTests</c> y el backfill en
/// <c>BackfillNoReabreOperacionesCerradasTests</c>.
/// </para>
/// </summary>
public class RevocacionCierraOperacionesOutboundAcotadasBajoRuntimeTests : IAsyncLifetime
{
    private const string Contrasena = "Arnes#2026Seguro";

    private readonly ActorMutable _actor = new();
    private readonly CurrentUserServiceMutable _sesion = new();
    private ArnesDeArranqueRuntime _arnes = null!;

    private Guid _propietario;
    private Guid _operador;
    private Guid _clienteUno;
    private Guid _clienteDos;

    private ApplicationUser _administradorPropietario = null!;
    private ApplicationUser _gestoraOperador = null!;
    private ApplicationUser _otroGestorOperador = null!;

    private Guid _delegacion;
    private Guid _operacionTenantEntero;
    private Guid _acotadaVigente;
    private Guid _acotadaProgramada;
    private Guid _inbound;

    public async Task InitializeAsync()
    {
        _arnes = await ArnesDeArranqueRuntime.CrearAsync(
            datosDePruebaActivos: false, actorAuditoriaPersonalizado: _actor, currentUserServicePersonalizado: _sesion);

        await using (var propietarioDeLaBase = ContextoPropietarioDeLaBase())
        {
            _propietario = await SembrarTenantAsync(propietarioDeLaBase, "Propietario", PerfilVocabularioTenant.ClienteDirecto);
            _operador = await SembrarTenantAsync(propietarioDeLaBase, "Operador CAE", PerfilVocabularioTenant.Consultora, operadorCaeExterno: true);
        }

        await using (var sembrado = ContextoPropietarioDeLaBase(_propietario))
        {
            var uno = Empresa.CrearComoCliente("Cliente acotado uno S.A.", "B10380202", false, null, null);
            var dos = Empresa.CrearComoCliente("Cliente acotado dos S.A.", "B10380228", false, null, null);
            sembrado.Empresas.AddRange(uno, dos);
            await sembrado.SaveChangesAsync();
            (_clienteUno, _clienteDos) = (uno.Id, dos.Id);
        }

        _administradorPropietario = await SembrarUsuarioAsync("admin-propietario", _propietario, Roles.Administrador);
        _gestoraOperador = await SembrarUsuarioAsync("gestora-operador", _operador, Roles.GestorCae);
        _otroGestorOperador = await SembrarUsuarioAsync("otro-gestor-operador", _operador, Roles.GestorCae);

        // La delegación y su operación del Tenant entero, como las deja el alta real.
        var alta = await AutorizarAsync(_administradorPropietario);
        alta.EsExitoso.Should().BeTrue(alta.EsFallido ? alta.Error.Codigo : string.Empty);
        _delegacion = alta.Valor;

        // Lo que ningún comando de hoy escribe y el modelo admite: dos operaciones Outbound acotadas
        // a un Cliente empresarial (una vigente, otra programada que el job activaría al llegar su
        // fecha) y una Inbound del mismo par. Las carteras son del Tenant entero (D-7) bajo cada
        // operación; la de la Gestora CAE bajo la del Tenant entero es la principal.
        await using (var propietarioDeLaBase = ContextoPropietarioDeLaBase())
        {
            var ahora = DateTime.UtcNow;
            var tenantEntero = await propietarioDeLaBase.AsignacionesOperacion.SingleAsync(o =>
                !o.EsRaiz && o.PropietarioTenantId == _propietario && o.OperadorTenantId == _operador
                && o.Servicio == ServicioCae.Outbound && o.Estado == EstadoAsignacion.Vigente);
            var acotadaVigente = AsignacionOperacion.Externa(_propietario, _operador, ServicioCae.Outbound,
                AmbitoAsignacion.DeRelacionCliente(_clienteUno), ahora.AddDays(-1), null, ahora);
            var acotadaProgramada = AsignacionOperacion.Externa(_propietario, _operador, ServicioCae.Outbound,
                AmbitoAsignacion.DeRelacionCliente(_clienteDos), ahora.AddDays(2), null, ahora);
            var inbound = AsignacionOperacion.Externa(_propietario, _operador, ServicioCae.Inbound,
                AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora);
            propietarioDeLaBase.AsignacionesOperacion.AddRange(acotadaVigente, acotadaProgramada, inbound);

            var principal = AsignacionCartera.Externa(
                tenantEntero, _gestoraOperador.Id, Roles.GestorCae, AmbitoAsignacion.Universal, ahora, null, ahora);
            principal.DesignarPrincipal();
            propietarioDeLaBase.AsignacionesCartera.AddRange(
                principal,
                AsignacionCartera.Externa(acotadaVigente, _gestoraOperador.Id, Roles.GestorCae, AmbitoAsignacion.Universal, ahora, null, ahora),
                AsignacionCartera.Externa(acotadaVigente, _otroGestorOperador.Id, Roles.GestorCae, AmbitoAsignacion.Universal, ahora, null, ahora),
                AsignacionCartera.Externa(acotadaProgramada, _gestoraOperador.Id, Roles.GestorCae, AmbitoAsignacion.Universal, ahora, null, ahora),
                AsignacionCartera.Externa(inbound, _gestoraOperador.Id, Roles.GestorCae, AmbitoAsignacion.Universal, ahora, null, ahora));
            propietarioDeLaBase.AsignacionesOperadorDelegadoConRevocadas.AddRange(
                new AsignacionOperadorDelegado(_delegacion, _gestoraOperador.Id, Roles.GestorCae),
                new AsignacionOperadorDelegado(_delegacion, _otroGestorOperador.Id, Roles.GestorCae));
            await propietarioDeLaBase.SaveChangesAsync();

            acotadaProgramada.Estado.Should().Be(EstadoAsignacion.Programada, "control: empieza en el futuro");
            (_operacionTenantEntero, _acotadaVigente, _acotadaProgramada, _inbound) =
                (tenantEntero.Id, acotadaVigente.Id, acotadaProgramada.Id, inbound.Id);
        }
    }

    public async Task DisposeAsync() => await _arnes.DisposeAsync();

    [Fact]
    public async Task Revocar_la_delegacion_cierra_tambien_las_operaciones_Outbound_acotadas_y_sus_carteras_bajo_RLS()
    {
        (await EstadoOperacionAsync(_acotadaVigente)).Should().Be(EstadoAsignacion.Vigente, "control: la acotada sigue abierta");
        (await CarterasNoCerradasAsync(_acotadaVigente)).Should().Be(2, "control: con sus dos carteras");
        (await EstadoOperacionAsync(_acotadaProgramada)).Should().Be(EstadoAsignacion.Programada);

        var revocar = await RevocarDelegacionAsync(_gestoraOperador, Roles.GestorCae);
        revocar.EsExitoso.Should().BeTrue(revocar.EsFallido ? revocar.Error.Codigo : string.Empty);

        foreach (var operacion in new[] { _operacionTenantEntero, _acotadaVigente, _acotadaProgramada })
        {
            (await EstadoOperacionAsync(operacion)).Should().Be(EstadoAsignacion.Cerrada,
                "revocar cierra toda Asignación de Operación Outbound del par, del Tenant entero o acotada");
            (await CarterasNoCerradasAsync(operacion)).Should().Be(0, "y todas sus carteras");
        }

        (await EstadoOperacionAsync(_inbound)).Should().Be(EstadoAsignacion.Vigente, "Inbound tiene su propio ciclo");
        (await CarterasNoCerradasAsync(_inbound)).Should().Be(1);
    }

    [Fact]
    public async Task Retirar_a_la_persona_cierra_sus_carteras_tambien_bajo_las_operaciones_Outbound_acotadas_bajo_RLS()
    {
        (await CarterasNoCerradasAsync(_acotadaVigente, _gestoraOperador.Id)).Should().Be(1, "control: cartera abierta bajo la acotada");

        var asignacion = await AsignacionOperadorAsync(_gestoraOperador.Id);
        var retirar = await RetirarPersonaAsync(_administradorPropietario, Roles.Administrador, asignacion);
        retirar.EsExitoso.Should().BeTrue(retirar.EsFallido ? retirar.Error.Codigo : string.Empty);

        foreach (var operacion in new[] { _operacionTenantEntero, _acotadaVigente, _acotadaProgramada })
            (await CarterasNoCerradasAsync(operacion, _gestoraOperador.Id)).Should().Be(0,
                "retirar a la persona le cierra todas sus carteras Outbound del par, de Tenant entero o acotadas");

        (await CarterasNoCerradasAsync(_acotadaVigente, _otroGestorOperador.Id)).Should().Be(1, "la de otra persona sigue");
        (await EstadoOperacionAsync(_acotadaVigente)).Should().Be(EstadoAsignacion.Vigente, "retirar no cierra la operación");
        (await CarterasNoCerradasAsync(_inbound, _gestoraOperador.Id)).Should().Be(1, "Inbound tiene su propio ciclo");
    }

    [Fact]
    public async Task Reactivar_solo_reabre_la_del_Tenant_entero_y_devuelve_la_marca_aunque_una_acotada_cerrase_despues_bajo_RLS()
    {
        // La programada se cierra con su inicio futuro como fin de vigencia: es la última cerrada del
        // par por fecha. Si la reactivación la tomara como «anterior», no encontraría lo que cerró la
        // cascada de la del Tenant entero y la Gestora CAE perdería su cartera y su marca.
        (await RevocarDelegacionAsync(_gestoraOperador, Roles.GestorCae)).EsExitoso.Should().BeTrue();

        var reactivar = await ReactivarAsync(_administradorPropietario, Roles.Administrador);
        reactivar.EsExitoso.Should().BeTrue(reactivar.EsFallido ? reactivar.Error.Codigo : string.Empty);

        await using var verificacion = ContextoPropietarioDeLaBase();
        var abiertas = await verificacion.AsignacionesOperacion
            .Where(o => !o.EsRaiz && o.PropietarioTenantId == _propietario && o.OperadorTenantId == _operador
                        && o.Servicio == ServicioCae.Outbound && o.Estado != EstadoAsignacion.Cerrada)
            .ToListAsync();
        abiertas.Should().ContainSingle("reactivar reabre una sola operación, la del Tenant entero")
            .Which.Ambito.EsUniversal.Should().BeTrue();
        (await EstadoOperacionAsync(_acotadaVigente)).Should().Be(EstadoAsignacion.Cerrada, "las acotadas necesitan un acto explícito");
        (await EstadoOperacionAsync(_acotadaProgramada)).Should().Be(EstadoAsignacion.Cerrada);

        var repuesta = await verificacion.AsignacionesCartera.IgnoreQueryFilters()
            .SingleAsync(c => c.AsignacionOperacionId == abiertas[0].Id && c.UsuarioId == _gestoraOperador.Id);
        repuesta.Estado.Should().Be(EstadoAsignacion.Vigente);
        repuesta.EsPrincipal.Should().BeTrue("vuelve a ser principal quien lo era (D-9)");
    }

    // ── Montaje ──────────────────────────────────────────────────────────────

    private async Task<Result<Guid>> AutorizarAsync(ApplicationUser usuario)
    {
        using var ambito = _arnes.Servicios.CreateScope();
        var servicios = ambito.ServiceProvider;
        var contexto = servicios.GetRequiredService<CaeManagerDbContext>();
        var usuarioActual = FijarSesion(usuario, Roles.Administrador);
        var tenant = servicios.GetRequiredService<ITenantActual>();

        var handler = new CrearDelegacionTenantCommandHandler(
            new DelegacionTenantRepository(contexto), contexto,
            new AsignacionesOperativasWriter(contexto, tenant, usuarioActual),
            Autorizacion(servicios), usuarioActual, contexto, new TransaccionDeComando(contexto),
            new AsignacionAutomaticaInerte(), tenant);

        using (AmbitoTenantExplicito.Establecer(usuario.TenantId))
            return await handler.Handle(new CrearDelegacionTenantCommand(_operador, _propietario), CancellationToken.None);
    }

    private async Task<Result> RevocarDelegacionAsync(ApplicationUser usuario, string rol)
    {
        using var ambito = _arnes.Servicios.CreateScope();
        var servicios = ambito.ServiceProvider;
        var contexto = servicios.GetRequiredService<CaeManagerDbContext>();
        var usuarioActual = FijarSesion(usuario, rol);
        var tenant = servicios.GetRequiredService<ITenantActual>();
        var handler = new DesactivarDelegacionTenantCommandHandler(
            new DelegacionTenantRepository(contexto), usuarioActual,
            new AsignacionesOperativasWriter(contexto, tenant, usuarioActual), contexto, tenant,
            Directorio(servicios, contexto, tenant, usuarioActual), contexto,
            new CatalogoIncorporacionCartera(contexto, usuarioActual));

        using (AmbitoTenantExplicito.Establecer(usuario.TenantId))
            return await handler.Handle(new DesactivarDelegacionTenantCommand(_delegacion), CancellationToken.None);
    }

    private async Task<Result> RetirarPersonaAsync(ApplicationUser usuario, string rol, Guid asignacionId)
    {
        using var ambito = _arnes.Servicios.CreateScope();
        var servicios = ambito.ServiceProvider;
        var contexto = servicios.GetRequiredService<CaeManagerDbContext>();
        var usuarioActual = FijarSesion(usuario, rol);
        var tenant = servicios.GetRequiredService<ITenantActual>();
        var handler = new RevocarAsignacionOperadorDelegadoCommandHandler(
            new AsignacionOperadorDelegadoRepository(contexto), new DelegacionTenantRepository(contexto), usuarioActual,
            new AsignacionesOperativasWriter(contexto, tenant, usuarioActual), contexto, tenant, Autorizacion(servicios));

        using (AmbitoTenantExplicito.Establecer(usuario.TenantId))
            return await handler.Handle(new RevocarAsignacionOperadorDelegadoCommand(asignacionId), CancellationToken.None);
    }

    private async Task<Result> ReactivarAsync(ApplicationUser usuario, string rol)
    {
        using var ambito = _arnes.Servicios.CreateScope();
        var servicios = ambito.ServiceProvider;
        var contexto = servicios.GetRequiredService<CaeManagerDbContext>();
        var usuarioActual = FijarSesion(usuario, rol);
        var tenant = servicios.GetRequiredService<ITenantActual>();
        var directorio = Directorio(servicios, contexto, tenant, usuarioActual);
        var handler = new ReactivarDelegacionTenantCommandHandler(
            new DelegacionTenantRepository(contexto), usuarioActual,
            new AsignacionesOperativasWriter(contexto, tenant, usuarioActual), contexto, contexto,
            new TransaccionDeComando(contexto), new CatalogoIncorporacionCartera(contexto, usuarioActual),
            directorio, directorio, new BloqueoCarteraUsuario(contexto), tenant, Autorizacion(servicios));

        using (AmbitoTenantExplicito.Establecer(usuario.TenantId))
            return await handler.Handle(new ReactivarDelegacionTenantCommand(_delegacion), CancellationToken.None);
    }

    private CurrentUserServiceFalso FijarSesion(ApplicationUser usuario, string rol)
    {
        _actor.Actual = usuario.Id;
        _sesion.UsuarioId = usuario.Id;
        _sesion.TenantOrigenId = usuario.TenantId;
        _sesion.Rol = rol;
        return new CurrentUserServiceFalso(usuario.Id, rol, usuario.TenantId);
    }

    private static AutorizacionDelegacionPorAdministradorDelCliente Autorizacion(IServiceProvider servicios) =>
        new(servicios.GetRequiredService<UserManager<ApplicationUser>>());

    private static DirectorioUsuariosTenant Directorio(
        IServiceProvider servicios, CaeManagerDbContext contexto, ITenantActual tenant, ICurrentUserService usuario) =>
        new(servicios.GetRequiredService<UserManager<ApplicationUser>>(), contexto, tenant, new PuertaAccesoDatos(), contexto, usuario);

    private async Task<Guid> AsignacionOperadorAsync(Guid usuarioId)
    {
        await using var verificacion = ContextoPropietarioDeLaBase();
        return await verificacion.AsignacionesOperadorDelegadoConRevocadas
            .Where(a => a.DelegacionTenantId == _delegacion && a.UsuarioId == usuarioId)
            .Select(a => a.Id)
            .SingleAsync();
    }

    private async Task<EstadoAsignacion> EstadoOperacionAsync(Guid operacionId)
    {
        await using var verificacion = ContextoPropietarioDeLaBase();
        return (await verificacion.AsignacionesOperacion.SingleAsync(o => o.Id == operacionId)).Estado;
    }

    private async Task<int> CarterasNoCerradasAsync(Guid operacionId, Guid? usuarioId = null)
    {
        await using var verificacion = ContextoPropietarioDeLaBase();
        return await verificacion.AsignacionesCartera.IgnoreQueryFilters().CountAsync(c =>
            c.AsignacionOperacionId == operacionId
            && (usuarioId == null || c.UsuarioId == usuarioId)
            && c.Estado != EstadoAsignacion.Cerrada);
    }

    private async Task<ApplicationUser> SembrarUsuarioAsync(string alias, Guid tenant, string rol)
    {
        using var ambito = _arnes.Servicios.CreateScope();
        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"{alias}-{Guid.NewGuid():N}@caemanager.local";
        var usuario = new ApplicationUser
        {
            UserName = email,
            Email = email,
            NombreCompleto = alias,
            EmailConfirmed = true,
            TenantId = tenant,
        };

        using (AmbitoTenantExplicito.Establecer(tenant))
        {
            (await userManager.CreateAsync(usuario, Contrasena)).Succeeded.Should().BeTrue();
            (await userManager.AddToRoleAsync(usuario, rol)).Succeeded.Should().BeTrue();
        }

        return usuario;
    }

    private static async Task<Guid> SembrarTenantAsync(
        CaeManagerDbContext contexto, string prefijo, PerfilVocabularioTenant perfil, bool operadorCaeExterno = false)
    {
        var tenant = new Tenant($"{prefijo} {Guid.NewGuid():N}", perfil);
        if (operadorCaeExterno)
            tenant.HabilitarComoOperadorCaeExterno();
        contexto.Tenants.Add(tenant);
        await contexto.SaveChangesAsync();
        return tenant.Id;
    }

    /// <summary>
    /// Rol propietario de la base: solo siembra y comprueba. Con <paramref name="tenantSellado"/>,
    /// sella el Tenant de las filas que lo llevan (las Empresas). Lo que se prueba corre siempre por
    /// el arnés, como <c>cae_app_runtime</c>.
    /// </summary>
    private CaeManagerDbContext ContextoPropietarioDeLaBase(Guid? tenantSellado = null)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantSellado };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_arnes.CadenaPropietario)
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;
        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class ActorMutable : IActorAuditoria
    {
        public Guid? Actual { get; set; }
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(ActorAuditoria.Normal(Actual));
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => ActorAuditoria.Normal(Actual);
    }
}
