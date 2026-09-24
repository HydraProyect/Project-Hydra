using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Commands.CrearDelegacionTenant;
using CaeManager.Application.Tenants.Queries.AutorizarOperadorCaeExterno;
using CaeManager.Domain.Common;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
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

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// Incremento 1b del aprovisionamiento (opción 1′): el Administrador de un Tenant
/// propietario YA existente autoriza a un Operador CAE externo con
/// <see cref="CrearDelegacionTenantCommand"/>. Contra Postgres real, con el cableado
/// de producción del <see cref="ArnesDeArranqueRuntime"/> (rol <c>cae_app_runtime</c>,
/// los cuatro interceptores, RLS sin debilitar) y con Identity real.
///
/// <para>
/// Cada persona actúa desde SU propio workspace activo (<c>app.tenant_id</c> = el
/// Tenant de su cuenta), que es como llega en producción. Así, un «no» no puede
/// venir de que RLS le esconda su propia fila de usuario: viene del predicado de
/// <see cref="AutorizacionDelegacionPorAdministradorDelCliente"/>, que es lo que
/// se quiere probar. La persona que ejecuta la delegación con éxito es la misma
/// cuyo Actor real queda en la auditoría: el clic del Administrador es la
/// instrucción documentada (RGPD art. 28, decisión del propietario 2026-09-22).
/// </para>
/// </summary>
public class AutorizarOperadorCaeExternoRlsTests : IAsyncLifetime
{
    private const string Contrasena = "Arnes#2026Seguro";

    private readonly ActorMutable _actor = new();
    private ArnesDeArranqueRuntime _arnes = null!;

    private Guid _propietario;
    private Guid _operador;
    private Guid _otroPropietario;
    private Guid _plataforma;

    private ApplicationUser _administradorPropietario = null!;
    private ApplicationUser _gestorPropietario = null!;
    private ApplicationUser _administradorOtroTenant = null!;
    private ApplicationUser _actorPlataforma = null!;

    public async Task InitializeAsync()
    {
        _arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false, actorAuditoriaPersonalizado: _actor);

        await using (var propietarioDeLaBase = ContextoPropietarioDeLaBase())
        {
            _propietario = await SembrarTenantAsync(propietarioDeLaBase, "Refrielectric", PerfilVocabularioTenant.ClienteDirecto);
            _operador = await SembrarTenantAsync(propietarioDeLaBase, "ArcoSPA", PerfilVocabularioTenant.Consultora, operadorCaeExterno: true);
            _otroPropietario = await SembrarTenantAsync(propietarioDeLaBase, "Laboratorios Dexter", PerfilVocabularioTenant.ClienteDirecto);
            _plataforma = (await propietarioDeLaBase.Tenants.SingleAsync(t => t.EsPlataforma)).Id;
        }

        _administradorPropietario = await SembrarUsuarioAsync("admin-refrielectric", _propietario, Roles.Administrador);
        _gestorPropietario = await SembrarUsuarioAsync("gestor-refrielectric", _propietario, Roles.GestorCae);
        _administradorOtroTenant = await SembrarUsuarioAsync("admin-dexter", _otroPropietario, Roles.Administrador);
        // El Actor de Plataforma TALVEG con TODO lo que podría confundirse con
        // autoridad: rol Administrador en su Tenant y concesión AdminPlataforma
        // global y vigente. Ninguna de las dos le deja ejecutar la delegación.
        _actorPlataforma = await SembrarUsuarioAsync("actor-plataforma", _plataforma, Roles.Administrador);
        await using (var propietarioDeLaBase = ContextoPropietarioDeLaBase())
        {
            propietarioDeLaBase.ConcesionesPrivilegio.Add(ConcesionPrivilegio.Global(
                _actorPlataforma.Id, vigenciaDesde: DateTime.UtcNow.AddMinutes(-5), vigenciaHasta: null));
            await propietarioDeLaBase.SaveChangesAsync();
        }
    }

    public async Task DisposeAsync() => await _arnes.DisposeAsync();

    // ── Autorización real (Identity + base), persona por persona ───────────

    [Fact]
    public async Task Solo_el_administrador_del_tenant_propietario_puede_autorizar_al_operador()
    {
        (await PuedeAutorizarAsync(_administradorPropietario)).Should().BeTrue();
        (await PuedeAutorizarAsync(_gestorPropietario)).Should().BeFalse(
            "un Gestor CAE del Tenant propietario está en el Tenant correcto sin autoridad para conceder acceso");
        (await PuedeAutorizarAsync(_administradorOtroTenant)).Should().BeFalse(
            "el rol Administrador de otro Tenant no da autoridad sobre este");
        (await PuedeAutorizarAsync(_actorPlataforma)).Should().BeFalse(
            "TALVEG nunca inicia ni ejecuta la delegación (ADR-004 § 11.1)");
    }

    [Fact]
    public async Task El_actor_de_plataforma_tampoco_puede_desde_dentro_del_workspace_del_propietario()
    {
        // Workspace activo = Tenant propietario (p. ej. una ventana de soporte):
        // una coordenada de contexto no es autoridad.
        (await PuedeAutorizarAsync(_actorPlataforma, workspaceActivo: _propietario)).Should().BeFalse();
    }

    [Fact]
    public async Task Un_administrador_desactivado_no_puede_autorizar()
    {
        // Hallazgo de la revisión puente del incremento 1b: sin esta guarda,
        // una cuenta desactivada por /usuarios seguía teniendo rol Administrador
        // y tenant correcto, y superaba la autorización mientras su cookie o
        // token de sesión ya emitidos siguieran vivos.
        using (var ambito = _arnes.Servicios.CreateScope())
        {
            var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var usuario = (await userManager.FindByIdAsync(_administradorPropietario.Id.ToString()))!;
            usuario.Desactivar();
            (await userManager.UpdateAsync(usuario)).Succeeded.Should().BeTrue();
        }

        (await PuedeAutorizarAsync(_administradorPropietario)).Should().BeFalse();
    }

    // ── El comando completo bajo RLS ─────────────────────────────────────────

    [Fact]
    public async Task El_administrador_autoriza_y_queda_la_delegacion_la_operacion_y_su_actor_real()
    {
        var resultado = await EjecutarAsync(_administradorPropietario, _operador, _propietario);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : string.Empty);

        await using var propietarioDeLaBase = ContextoPropietarioDeLaBase();
        var vinculo = await propietarioDeLaBase.DelegacionesTenant.SingleAsync(d => d.Id == resultado.Valor);
        vinculo.TenantConsultoraId.Should().Be(_operador);
        vinculo.TenantClienteId.Should().Be(_propietario);
        vinculo.Proposito.Should().Be(PropositoDelegacion.OperadorExterno);

        (await propietarioDeLaBase.AsignacionesOperacion.CountAsync(o =>
                !o.EsRaiz && o.PropietarioTenantId == _propietario && o.OperadorTenantId == _operador
                && o.Estado == EstadoAsignacion.Vigente))
            .Should().Be(1);

        // Nadie queda como Gestor CAE: eso lo decide después el Operador con su cartera.
        (await propietarioDeLaBase.AsignacionesOperadorDelegado.AnyAsync(a => a.DelegacionTenantId == vinculo.Id))
            .Should().BeFalse();

        (await propietarioDeLaBase.RegistrosAuditoria.IgnoreQueryFilters()
                .AnyAsync(r => r.EntidadTipo == nameof(DelegacionTenant) && r.EntidadId == vinculo.Id
                               && r.ActorRealUsuarioId == _administradorPropietario.Id))
            .Should().BeTrue("el consentimiento es el clic del Administrador, y su Actor real queda registrado");
    }

    [Fact]
    public async Task Ni_el_gestor_ni_otro_tenant_ni_la_plataforma_escriben_nada()
    {
        (await EjecutarAsync(_gestorPropietario, _operador, _propietario)).Error.Codigo
            .Should().Be("DelegacionTenant.NoAutorizado");
        (await EjecutarAsync(_administradorOtroTenant, _operador, _propietario)).Error.Codigo
            .Should().Be("DelegacionTenant.NoAutorizado");
        (await EjecutarAsync(_actorPlataforma, _operador, _propietario)).Error.Codigo
            .Should().Be("DelegacionTenant.NoAutorizado");
        (await EjecutarAsync(_actorPlataforma, _operador, _propietario, workspaceActivo: _propietario)).Error.Codigo
            .Should().Be("DelegacionTenant.NoAutorizado");

        await using var propietarioDeLaBase = ContextoPropietarioDeLaBase();
        (await propietarioDeLaBase.DelegacionesTenant.AnyAsync(d => d.TenantClienteId == _propietario)).Should().BeFalse();
        (await propietarioDeLaBase.AsignacionesOperacion.AnyAsync(o => !o.EsRaiz && o.PropietarioTenantId == _propietario))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Desde_el_workspace_de_otro_tenant_RLS_no_deja_escribir_la_operacion()
    {
        // El Administrador del propietario tiene la autoridad, pero su workspace
        // activo es otro Tenant: la operación delegada se sellaría contra un
        // propietario que no es el contextual y la política WITH CHECK la corta.
        // RLS no se debilita para acomodar esto; la pantalla oculta el botón fuera
        // de la organización propia.
        var accion = () => EjecutarAsync(_administradorPropietario, _operador, _propietario, workspaceActivo: _otroPropietario);

        await accion.Should().ThrowAsync<DbUpdateException>();

        await using var propietarioDeLaBase = ContextoPropietarioDeLaBase();
        (await propietarioDeLaBase.DelegacionesTenant.AnyAsync(d => d.TenantClienteId == _propietario))
            .Should().BeFalse("la transacción es una sola: si cae la operación, cae también el vínculo");
    }

    [Fact]
    public async Task Un_segundo_operador_con_la_operacion_completa_se_rechaza_con_mensaje_y_no_con_excepcion()
    {
        Guid segundoOperador;
        await using (var propietarioDeLaBase = ContextoPropietarioDeLaBase())
            segundoOperador = await SembrarTenantAsync(propietarioDeLaBase, "Prevención Norte", PerfilVocabularioTenant.Consultora, operadorCaeExterno: true);

        (await EjecutarAsync(_administradorPropietario, _operador, _propietario)).EsExitoso.Should().BeTrue();

        var segundo = await EjecutarAsync(_administradorPropietario, segundoOperador, _propietario);

        segundo.Error.Codigo.Should().Be("DelegacionTenant.OtroOperadorVigente");
    }

    [Fact]
    public async Task La_busqueda_bajo_RLS_resuelve_el_operador_y_nada_mas()
    {
        string nombreOperador;
        await using (var propietarioDeLaBase = ContextoPropietarioDeLaBase())
            nombreOperador = await propietarioDeLaBase.Tenants.Where(t => t.Id == _operador).Select(t => t.Nombre).SingleAsync();

        // El nombre exacto, pero con otras mayúsculas y espacios alrededor: el buscador
        // no distingue mayúsculas; un fragmento no encuentra nada.
        var comoAdministrador = await BuscarAsync(_administradorPropietario, new BuscarOperadorCaeExternoAutorizableQuery(null, $"  {nombreOperador.ToUpperInvariant()} "));
        var porFragmento = await BuscarAsync(_administradorPropietario, new BuscarOperadorCaeExternoAutorizableQuery(null, "ArcoSPA"));
        var comoGestor = await BuscarAsync(_gestorPropietario, new BuscarOperadorCaeExternoAutorizableQuery(_operador, null));
        var laPlataforma = await BuscarAsync(_administradorPropietario, new BuscarOperadorCaeExternoAutorizableQuery(_plataforma, null));
        var otroPropietario = await BuscarAsync(_administradorPropietario, new BuscarOperadorCaeExternoAutorizableQuery(_otroPropietario, null));

        comoAdministrador.Should().NotBeNull();
        comoAdministrador!.TenantId.Should().Be(_operador);
        porFragmento.Should().BeNull("solo el nombre exacto: nunca se enumeran Operadores CAE externos por prefijo");
        comoGestor.Should().BeNull("sin autoridad la consulta no revela ni que el Operador existe");
        laPlataforma.Should().BeNull();
        otroPropietario.Should().BeNull();
    }

    // ── Montaje ──────────────────────────────────────────────────────────────

    private async Task<bool> PuedeAutorizarAsync(ApplicationUser usuario, Guid? workspaceActivo = null)
    {
        using var ambito = _arnes.Servicios.CreateScope();
        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        using (AmbitoTenantExplicito.Establecer(workspaceActivo ?? usuario.TenantId))
            return await new AutorizacionDelegacionPorAdministradorDelCliente(userManager)
                .PuedeGestionarDelegacionesAsync(usuario.Id, _propietario);
    }

    private async Task<Result<Guid>> EjecutarAsync(
        ApplicationUser usuario, Guid operador, Guid propietario, Guid? workspaceActivo = null)
    {
        using var ambito = _arnes.Servicios.CreateScope();
        var servicios = ambito.ServiceProvider;
        var contexto = servicios.GetRequiredService<CaeManagerDbContext>();
        var usuarioActual = new CurrentUserServiceFalso(usuario.Id, tenantOrigenId: usuario.TenantId);
        _actor.Actual = usuario.Id;

        var handler = new CrearDelegacionTenantCommandHandler(
            new DelegacionTenantRepository(contexto),
            contexto,
            new AsignacionesOperativasWriter(contexto, servicios.GetRequiredService<ITenantActual>(), usuarioActual),
            new AutorizacionDelegacionPorAdministradorDelCliente(servicios.GetRequiredService<UserManager<ApplicationUser>>()),
            usuarioActual,
            contexto);

        using (AmbitoTenantExplicito.Establecer(workspaceActivo ?? usuario.TenantId))
            return await handler.Handle(new CrearDelegacionTenantCommand(operador, propietario), CancellationToken.None);
    }

    private async Task<OperadorCaeExternoAutorizableDto?> BuscarAsync(
        ApplicationUser usuario, BuscarOperadorCaeExternoAutorizableQuery consulta)
    {
        using var ambito = _arnes.Servicios.CreateScope();
        var servicios = ambito.ServiceProvider;

        var handler = new AutorizarOperadorCaeExternoQueriesHandler(
            servicios.GetRequiredService<CaeManagerDbContext>(),
            new AutorizacionDelegacionPorAdministradorDelCliente(servicios.GetRequiredService<UserManager<ApplicationUser>>()),
            new CurrentUserServiceFalso(usuario.Id, tenantOrigenId: usuario.TenantId));

        using (AmbitoTenantExplicito.Establecer(usuario.TenantId))
            return await handler.Handle(consulta, CancellationToken.None);
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
    /// Rol propietario de la base, sin interceptores: solo siembra y comprueba. Lo
    /// que se prueba corre siempre por el arnés, como <c>cae_app_runtime</c>.
    /// </summary>
    private CaeManagerDbContext ContextoPropietarioDeLaBase()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_arnes.CadenaPropietario)
            .Options;
        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), new SinTenant());
    }

    private sealed class SinTenant : ITenantActual
    {
        public Guid? TenantId => null;
    }

    private sealed class ActorMutable : IActorAuditoria
    {
        public Guid? Actual { get; set; }
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(ActorAuditoria.Normal(Actual));
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => ActorAuditoria.Normal(Actual);
    }
}
