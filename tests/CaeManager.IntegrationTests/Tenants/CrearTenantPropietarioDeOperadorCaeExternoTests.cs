using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Commands.CrearTenantPropietarioDeOperadorCaeExterno;
using CaeManager.Application.Tenants.Queries.ObtenerOperadoresCaeExternos;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Soporte;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Operaciones;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using CaeManager.Infrastructure.Plataforma;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// Alta, por el Actor de Plataforma TALVEG, de un Tenant propietario nuevo bajo un
/// Operador CAE externo ya existente. Contra Postgres real, con el mismo montaje que
/// <see cref="CrearOperadorCaeExternoTests"/> y <see cref="CrearClienteDeleganteTests"/>,
/// más el <see cref="AuditoriaInterceptor"/> real: la propiedad crítica es que la
/// delegación cuelga del OPERADOR nombrado y no del tenant de origen de quien ejecuta
/// (TALVEG nunca pasa a figurar como Operador CAE), y que el Actor real queda en la traza.
/// </summary>
public class CrearTenantPropietarioDeOperadorCaeExternoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly ITenantActual _tenantActual = new TenantActualDesdeAmbitoExplicito();
    private Guid _actorReal = Guid.Empty;

    public async Task InitializeAsync()
    {
        await using var dbContext = CrearContexto();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync() =>
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private CaeManagerDbContext CrearContexto()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(
                new TenantSelladoInterceptor(_tenantActual),
                new AuditoriaInterceptor(new ActorAuditoriaFalso(() => _actorReal)))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), _tenantActual);
    }

    private static async Task SembrarAdminPlataformaGlobalAsync(CaeManagerDbContext contexto, Guid usuarioId)
    {
        contexto.ConcesionesPrivilegio.Add(ConcesionPrivilegio.Global(
            usuarioId, vigenciaDesde: DateTime.UtcNow.AddMinutes(-5), vigenciaHasta: null));
        await contexto.SaveChangesAsync();
    }

    private static async Task<Guid> SembrarTenantAsync(
        CaeManagerDbContext contexto, PerfilVocabularioTenant perfil, string prefijo, bool operadorCaeExterno = false)
    {
        var tenant = new Tenant($"{prefijo} {Guid.NewGuid():N}", perfil);
        // Capacidad declarada aparte del perfil a propósito, nunca inferida
        // de perfil == Consultora — es justo la inferencia que P11 elimina
        // del gate real, y este arnés no debe reintroducirla en la sombra.
        if (operadorCaeExterno)
            tenant.HabilitarComoOperadorCaeExterno();
        contexto.Tenants.Add(tenant);
        await contexto.SaveChangesAsync();
        return tenant.Id;
    }

    private CrearTenantPropietarioDeOperadorCaeExternoCommandHandler CrearHandler(
        CaeManagerDbContext contexto, Guid? usuarioId, IUnitOfWork? unitOfWork = null, ITenantsQueryContext? tenantsContext = null) =>
        CrearHandlerConUnidad(contexto, usuarioId, unitOfWork ?? contexto, tenantsContext ?? contexto);

    private CrearTenantPropietarioDeOperadorCaeExternoCommandHandler CrearHandlerConUnidad(
        CaeManagerDbContext contexto, Guid? usuarioId, IUnitOfWork unitOfWork, ITenantsQueryContext tenantsContext) =>
        new(
            new TenantRepository(contexto),
            tenantsContext,
            new DelegacionTenantRepository(contexto),
            new ParametroSistemaRepository(contexto),
            new AutorizacionAdminPlataformaPorConcesion(contexto),
            new CurrentUserServiceFalso(usuarioId),
            new AsignacionesOperativasWriter(contexto, _tenantActual, new CurrentUserServiceFalso(usuarioId)),
            unitOfWork);

    [Fact]
    public async Task El_administrador_de_plataforma_crea_un_tenant_propietario_operado_por_el_operador_nombrado()
    {
        await using var contexto = CrearContexto();
        var admin = Guid.NewGuid();
        _actorReal = admin;
        await SembrarAdminPlataformaGlobalAsync(contexto, admin);
        var operadorId = await SembrarTenantAsync(contexto, PerfilVocabularioTenant.Consultora, "ArcoSPA", operadorCaeExterno: true);

        var resultado = await CrearHandler(contexto, admin).Handle(
            new CrearTenantPropietarioDeOperadorCaeExternoCommand(operadorId, $" Laboratorios Dexter {Guid.NewGuid():N} "),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var propietarioId = resultado.Valor;

        var propietario = await contexto.Tenants.SingleAsync(t => t.Id == propietarioId);
        propietario.PerfilVocabulario.Should().Be(PerfilVocabularioTenant.ClienteDirecto);
        propietario.EsPlataforma.Should().BeFalse();
        propietario.Nombre.Should().Be(propietario.Nombre.Trim());

        var delegaciones = await contexto.DelegacionesTenant.Where(d => d.TenantClienteId == propietarioId).ToListAsync();
        var delegacion = delegaciones.Should().ContainSingle().Subject;
        delegacion.TenantConsultoraId.Should().Be(operadorId, "el Operador CAE lo nombra el comando, no sale del tenant de origen del ejecutor");
        delegacion.Activa.Should().BeTrue();
        delegacion.Proposito.Should().Be(PropositoDelegacion.OperadorExterno);

        // TALVEG (el Tenant de plataforma) no figura como Operador de nada.
        var plataformaId = (await contexto.Tenants.SingleAsync(t => t.EsPlataforma)).Id;
        (await contexto.DelegacionesTenant.AnyAsync(d => d.TenantConsultoraId == plataformaId && d.TenantClienteId == propietarioId))
            .Should().BeFalse();

        // No se asigna a nadie como Gestor CAE: el Actor de Plataforma nunca es Gestor ni Operador.
        (await contexto.AsignacionesOperadorDelegado.AnyAsync(a => a.DelegacionTenantId == delegacion.Id))
            .Should().BeFalse();

        // Operación raíz propia y operación delegada Operador → Tenant propietario.
        (await contexto.AsignacionesOperacion.AnyAsync(o => o.EsRaiz && o.PropietarioTenantId == propietarioId))
            .Should().BeTrue();
        (await contexto.AsignacionesOperacion.AnyAsync(
                o => !o.EsRaiz && o.PropietarioTenantId == propietarioId && o.OperadorTenantId == operadorId))
            .Should().BeTrue();

        (await contexto.ParametrosSistema.IgnoreQueryFilters().AnyAsync(p => p.TenantId == propietarioId))
            .Should().BeTrue();

        // Auditoría: el alta del Tenant y de su delegación llevan el Actor real.
        var registros = await contexto.RegistrosAuditoria.IgnoreQueryFilters()
            .Where(r => r.EntidadId == propietarioId || r.EntidadId == delegacion.Id)
            .ToListAsync();
        registros.Should().Contain(r => r.EntidadTipo == nameof(Tenant) && r.ActorRealUsuarioId == admin);
        registros.Should().Contain(r => r.EntidadTipo == nameof(DelegacionTenant) && r.ActorRealUsuarioId == admin);
    }

    [Fact]
    public async Task Rechaza_sin_concesion_de_administrador_de_plataforma_y_no_crea_nada()
    {
        await using var contexto = CrearContexto();
        var operadorId = await SembrarTenantAsync(contexto, PerfilVocabularioTenant.Consultora, "ArcoSPA", operadorCaeExterno: true);
        var tenantsAntes = await contexto.Tenants.CountAsync();

        var resultado = await CrearHandler(contexto, Guid.NewGuid()).Handle(
            new CrearTenantPropietarioDeOperadorCaeExternoCommand(operadorId, "Rechazado"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("TenantPropietarioDeOperador.SinPermiso");
        (await contexto.Tenants.CountAsync()).Should().Be(tenantsAntes);
        (await contexto.DelegacionesTenant.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Una_concesion_acotada_a_tenants_no_basta_porque_el_alcance_es_global()
    {
        await using var contexto = CrearContexto();
        var admin = Guid.NewGuid();
        var operadorId = await SembrarTenantAsync(contexto, PerfilVocabularioTenant.Consultora, "ArcoSPA", operadorCaeExterno: true);
        contexto.ConcesionesPrivilegio.Add(ConcesionPrivilegio.SobreTenants(
            admin, CapacidadPrivilegio.AdminPlataforma, [operadorId],
            vigenciaDesde: DateTime.UtcNow.AddMinutes(-5), vigenciaHasta: null));
        await contexto.SaveChangesAsync();

        var resultado = await CrearHandler(contexto, admin).Handle(
            new CrearTenantPropietarioDeOperadorCaeExternoCommand(operadorId, "Acotado"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("TenantPropietarioDeOperador.SinPermiso");
    }

    [Fact]
    public async Task Rechaza_un_operador_que_es_el_tenant_de_plataforma()
    {
        await using var contexto = CrearContexto();
        var admin = Guid.NewGuid();
        await SembrarAdminPlataformaGlobalAsync(contexto, admin);
        var plataforma = await contexto.Tenants.SingleAsync(t => t.EsPlataforma);
        // Con la capacidad concedida a propósito: así solo la guarda EsPlataforma
        // puede rechazarlo (sin esto, la guarda de capacidad lo taparía y el
        // test no distinguiría la mutación).
        plataforma.HabilitarComoOperadorCaeExterno();
        await contexto.SaveChangesAsync();
        var plataformaId = plataforma.Id;

        var resultado = await CrearHandler(contexto, admin).Handle(
            new CrearTenantPropietarioDeOperadorCaeExternoCommand(plataformaId, "Bajo TALVEG"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("TenantPropietarioDeOperador.OperadorNoValido", "TALVEG no es Operador CAE por defecto");
        (await contexto.DelegacionesTenant.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Rechaza_un_tenant_que_no_es_operador_y_un_id_inexistente()
    {
        await using var contexto = CrearContexto();
        var admin = Guid.NewGuid();
        await SembrarAdminPlataformaGlobalAsync(contexto, admin);
        var propietarioNoOperadorId = await SembrarTenantAsync(contexto, PerfilVocabularioTenant.ClienteDirecto, "Laboratorios Dexter");
        var handler = CrearHandler(contexto, admin);

        var comoOperador = await handler.Handle(
            new CrearTenantPropietarioDeOperadorCaeExternoCommand(propietarioNoOperadorId, "Bajo un cliente"), CancellationToken.None);
        var inexistente = await handler.Handle(
            new CrearTenantPropietarioDeOperadorCaeExternoCommand(Guid.NewGuid(), "Bajo nadie"), CancellationToken.None);

        comoOperador.Error.Codigo.Should().Be("TenantPropietarioDeOperador.OperadorNoValido");
        inexistente.Error.Codigo.Should().Be("TenantPropietarioDeOperador.OperadorNoValido");
        (await contexto.DelegacionesTenant.AnyAsync()).Should().BeFalse();
    }

    /// <summary>
    /// Falsación de P11: si el gate volviera a mirar el perfil de vocabulario
    /// en vez de la capacidad, este Tenant (perfil Consultora, sin la
    /// capacidad concedida) pasaría el gate indebidamente. Es la mutación que
    /// <see cref="Rechaza_un_operador_que_es_el_tenant_de_plataforma"/> no
    /// puede detectar por sí sola, porque ese test aísla la guarda EsPlataforma.
    /// </summary>
    [Fact]
    public async Task Rechaza_un_tenant_con_perfil_consultora_sin_la_capacidad_concedida()
    {
        await using var contexto = CrearContexto();
        var admin = Guid.NewGuid();
        await SembrarAdminPlataformaGlobalAsync(contexto, admin);
        var sinCapacidadId = await SembrarTenantAsync(contexto, PerfilVocabularioTenant.Consultora, "ArcoSPA");

        var resultado = await CrearHandler(contexto, admin).Handle(
            new CrearTenantPropietarioDeOperadorCaeExternoCommand(sinCapacidadId, "Bajo un Consultora sin capacidad"),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(
            "TenantPropietarioDeOperador.OperadorNoValido", "el perfil de vocabulario ya no es autoridad, solo la capacidad concedida");
        (await contexto.DelegacionesTenant.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Rechaza_un_nombre_duplicado_aunque_difiera_en_espacios()
    {
        await using var contexto = CrearContexto();
        var admin = Guid.NewGuid();
        await SembrarAdminPlataformaGlobalAsync(contexto, admin);
        var operadorId = await SembrarTenantAsync(contexto, PerfilVocabularioTenant.Consultora, "ArcoSPA", operadorCaeExterno: true);
        var nombre = $"Laboratorios Dexter {Guid.NewGuid():N}";
        contexto.Tenants.Add(new Tenant(nombre));
        await contexto.SaveChangesAsync();

        var resultado = await CrearHandler(contexto, admin).Handle(
            new CrearTenantPropietarioDeOperadorCaeExternoCommand(operadorId, $"  {nombre}  "), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("TenantPropietarioDeOperador.NombreDuplicado");
    }

    [Fact]
    public async Task La_consulta_lista_operadores_con_sus_tenants_y_no_cuenta_soporte_ni_revocadas()
    {
        await using var contexto = CrearContexto();
        var admin = Guid.NewGuid();
        await SembrarAdminPlataformaGlobalAsync(contexto, admin);
        var operadorId = await SembrarTenantAsync(contexto, PerfilVocabularioTenant.Consultora, "ArcoSPA", operadorCaeExterno: true);
        var handler = CrearHandler(contexto, admin);
        var activo = await handler.Handle(
            new CrearTenantPropietarioDeOperadorCaeExternoCommand(operadorId, $"Activo {Guid.NewGuid():N}"), CancellationToken.None);
        var revocado = await handler.Handle(
            new CrearTenantPropietarioDeOperadorCaeExternoCommand(operadorId, $"Revocado {Guid.NewGuid():N}"), CancellationToken.None);
        var delegacionRevocada = await contexto.DelegacionesTenant.SingleAsync(d => d.TenantClienteId == revocado.Valor);
        delegacionRevocada.Desactivar();
        var plataformaId = (await contexto.Tenants.SingleAsync(t => t.EsPlataforma)).Id;
        contexto.DelegacionesTenant.Add(DelegacionTenant.ParaSoporte(plataformaId, activo.Valor));
        await contexto.SaveChangesAsync();

        var consulta = new ObtenerOperadoresCaeExternosQueryHandler(
            contexto, new AutorizacionAdminPlataformaPorConcesion(contexto), new CurrentUserServiceFalso(admin));
        var operadores = await consulta.Handle(new ObtenerOperadoresCaeExternosQuery(), CancellationToken.None);

        var operador = operadores.Should().ContainSingle(o => o.TenantId == operadorId).Subject;
        operador.TenantsPropietarios.Select(t => t.TenantId).Should().Equal(activo.Valor);
        operadores.Should().NotContain(o => o.TenantId == plataformaId, "el Tenant de plataforma no es un Operador CAE");
    }

    /// <summary>
    /// Falsación de P11: si el filtro de la consulta volviera a mirar el
    /// perfil de vocabulario en vez de la capacidad, este Tenant (perfil
    /// Consultora, sin la capacidad concedida) aparecería igualmente en
    /// <c>/delegaciones</c> aunque nunca pasó por el alta de Operador.
    /// </summary>
    [Fact]
    public async Task La_consulta_no_lista_un_tenant_con_perfil_consultora_sin_la_capacidad_concedida()
    {
        await using var contexto = CrearContexto();
        var admin = Guid.NewGuid();
        await SembrarAdminPlataformaGlobalAsync(contexto, admin);
        var sinCapacidadId = await SembrarTenantAsync(contexto, PerfilVocabularioTenant.Consultora, "ArcoSPA");

        var consulta = new ObtenerOperadoresCaeExternosQueryHandler(
            contexto, new AutorizacionAdminPlataformaPorConcesion(contexto), new CurrentUserServiceFalso(admin));
        var operadores = await consulta.Handle(new ObtenerOperadoresCaeExternosQuery(), CancellationToken.None);

        operadores.Should().NotContain(o => o.TenantId == sinCapacidadId, "el perfil de vocabulario ya no es autoridad, solo la capacidad concedida");
    }

    [Fact]
    public async Task La_consulta_devuelve_vacio_a_quien_no_es_administrador_de_plataforma()
    {
        await using var contexto = CrearContexto();
        await SembrarTenantAsync(contexto, PerfilVocabularioTenant.Consultora, "ArcoSPA", operadorCaeExterno: true);

        var consulta = new ObtenerOperadoresCaeExternosQueryHandler(
            new ContextoQueNoDebeLeerse(), new AutorizacionAdminPlataformaPorConcesion(contexto), new CurrentUserServiceFalso(Guid.NewGuid()));

        (await consulta.Handle(new ObtenerOperadoresCaeExternosQuery(), CancellationToken.None)).Should().BeEmpty();
    }

    /// <summary>
    /// Atomicidad: el alta confirma Tenant, ParametroSistema, operación raíz, DelegacionTenant y
    /// operación delegada en UN solo guardado (una transacción). Con dos guardados un fallo entre
    /// ambos dejaría un Tenant propietario sin Operador CAE externo (hallazgo de Codex, pasada 1).
    /// Falsación: volver a partir el guardado en dos hace que este contador dé 2.
    /// </summary>
    [Fact]
    public async Task El_alta_se_confirma_en_un_unico_guardado()
    {
        await using var contexto = CrearContexto();
        var admin = Guid.NewGuid();
        _actorReal = admin;
        await SembrarAdminPlataformaGlobalAsync(contexto, admin);
        var operadorId = await SembrarTenantAsync(contexto, PerfilVocabularioTenant.Consultora, "ArcoSPA", operadorCaeExterno: true);
        var unidad = new UnidadDeTrabajoContadora(contexto);

        var resultado = await CrearHandler(contexto, admin, unidad).Handle(
            new CrearTenantPropietarioDeOperadorCaeExternoCommand(operadorId, "Transportes Planet Express"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        unidad.Guardados.Should().Be(1);
        (await contexto.DelegacionesTenant.CountAsync(d => d.TenantClienteId == resultado.Valor)).Should().Be(1,
            "control positivo: el único guardado llevó también la delegación");
    }

    /// <summary>
    /// Quien no tiene la concesión no distingue por el mensaje qué Ids son Operadores reales:
    /// el error es el mismo para un Operador existente y para un Id inexistente.
    /// </summary>
    [Fact]
    public async Task Sin_concesion_el_error_es_identico_para_un_operador_real_y_un_id_inexistente()
    {
        await using var contexto = CrearContexto();
        var operadorReal = await SembrarTenantAsync(contexto, PerfilVocabularioTenant.Consultora, "ArcoSPA", operadorCaeExterno: true);
        var sinConcesion = Guid.NewGuid();

        var real = await CrearHandler(contexto, sinConcesion, tenantsContext: new ContextoQueNoDebeLeerse()).Handle(
            new CrearTenantPropietarioDeOperadorCaeExternoCommand(operadorReal, "Hostelería Krusty Krab"), CancellationToken.None);
        var inexistente = await CrearHandler(contexto, sinConcesion, tenantsContext: new ContextoQueNoDebeLeerse()).Handle(
            new CrearTenantPropietarioDeOperadorCaeExternoCommand(Guid.NewGuid(), "Hostelería Krusty Krab"), CancellationToken.None);

        real.Error.Should().Be(inexistente.Error);
        real.Error.Codigo.Should().Be("TenantPropietarioDeOperador.SinPermiso");
    }

    /// <summary>
    /// Lanza si alguien lee un catálogo transversal: sin concesión, la autorización tiene que
    /// cortar ANTES de cualquier lectura (Codex, pasada 2: comparar solo el error final no
    /// distinguía mover la consulta del Operador antes de la guarda).
    /// </summary>
    private sealed class ContextoQueNoDebeLeerse : ITenantsQueryContext
    {
        private static IQueryable<T> Prohibido<T>() => throw new InvalidOperationException("Lectura transversal antes de autorizar.");
        public IQueryable<Tenant> Tenants => Prohibido<Tenant>();
        public IQueryable<DelegacionTenant> DelegacionesTenant => Prohibido<DelegacionTenant>();
        public IQueryable<AsignacionOperadorDelegado> AsignacionesOperadorDelegado => Prohibido<AsignacionOperadorDelegado>();
        public IQueryable<RegistroActividadSoporte> RegistrosActividadSoporte => Prohibido<RegistroActividadSoporte>();
    }

    private sealed class UnidadDeTrabajoContadora(IUnitOfWork interna) : IUnitOfWork
    {
        public int Guardados { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Guardados++;
            return interna.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class CurrentUserServiceFalso(Guid? usuarioId) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult(usuarioId);
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(null);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class ActorAuditoriaFalso(Func<Guid> actorReal) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(ActorAuditoria.Normal(actorReal()));
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => ActorAuditoria.Normal(actorReal());
    }

    private sealed class TenantActualDesdeAmbitoExplicito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }
}
