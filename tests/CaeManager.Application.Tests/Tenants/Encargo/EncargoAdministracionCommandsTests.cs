using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Commands.RegistrarEncargoAdministracion;
using CaeManager.Application.Tenants.Commands.RetirarEncargoAdministracion;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Application.Tenants.Queries.ObtenerEncargosAdministracion;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Tenants;
using FluentAssertions;

namespace CaeManager.Application.Tests.Tenants.Encargo;

/// <summary>
/// El tercer acto excluido del Encargo de administración (decisión D-8, 2026-10-08): registrarlo y
/// retirarlo solo lo hace un Administrador miembro del Tenant propietario o Soporte TALVEG en una
/// Sesión Privilegiada de aprovisionamiento sobre ese Tenant. Nunca el rol efectivo, y nunca nadie
/// del Operador CAE que lo recibe — tampoco su Administrador con el rol elevado por el encargo.
/// </summary>
public class EncargoAdministracionCommandsTests
{
    private static readonly DateTime Ahora = new(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc);
    private const string Clausula = "Cláusula 7.2 del contrato de servicios";

    private readonly Tenant _propietario = new("Laboratorios Dexter", PerfilVocabularioTenant.ClienteDirecto);
    private readonly Tenant _operador = new("Planet Express Prevención", PerfilVocabularioTenant.ClienteDirecto);
    private readonly Guid _plataforma = Guid.NewGuid();
    private readonly Guid _otroTenant = Guid.NewGuid();
    private readonly RepositorioFalso _repositorio = new();
    private readonly AsignacionOperacion _operacion;

    public EncargoAdministracionCommandsTests()
    {
        _operacion = AsignacionOperacion.Externa(
            Propietario, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, Ahora.AddDays(-30), null, Ahora.AddDays(-30));
        _repositorio.Operaciones.Add(_operacion);
    }

    private Guid Propietario => _propietario.Id;
    private Guid Operador => _operador.Id;

    // --- Quién actúa ---------------------------------------------------------------------------

    /// <summary>Administrador miembro del Tenant propietario, en su Tenant.</summary>
    private Actor AdministradorPropio() => new(Guid.NewGuid(), Propietario, EsAdministradorMiembro: true);

    /// <summary>Soporte TALVEG con una Sesión Privilegiada de la capacidad dada sobre el Tenant dado.</summary>
    private Actor Soporte(CapacidadPrivilegio capacidad, Guid? tenantObjetivo = null) =>
        new(Guid.NewGuid(), _plataforma, EsAdministradorMiembro: false,
            Sesion: new SesionPrivilegiadaActiva(Guid.NewGuid(), Guid.NewGuid(), tenantObjetivo ?? Propietario, capacidad, null));

    /// <summary>
    /// Administrador del Operador CAE externo que administra por encargo: su rol efectivo en el
    /// Tenant propietario ES Administrador. No es miembro del Tenant propietario.
    /// </summary>
    private Actor AdministradorDelOperadorCaePorEncargo() => new(Guid.NewGuid(), Operador, EsAdministradorMiembro: false);

    private sealed record Actor(
        Guid UsuarioId, Guid? TenantOrigen, bool EsAdministradorMiembro,
        SesionPrivilegiadaActiva? Sesion = null, Guid? UsuarioSimulado = null);

    private AutoridadSobreElEncargo Autoridad(Actor actor) => new(
        new SesionFija(actor.Sesion),
        // El rol efectivo es Administrador para todos: ninguna decisión de este acto puede depender de él.
        new CurrentUserServiceFalso(actor.UsuarioId, "Administrador", actor.TenantOrigen),
        new ActorFijo(new ActorAuditoria(actor.UsuarioId, actor.UsuarioSimulado, TipoViaAcceso.Normal, null)),
        new PertenenciaFija(actor.EsAdministradorMiembro));

    private Task<CaeManager.Domain.Common.Result<Guid>> RegistrarAsync(
        Actor actor, Guid? operacionId = null, string clausula = Clausula, DateTime? vigenciaHasta = null, Guid? tenantActivo = null) =>
        new RegistrarEncargoAdministracionCommandHandler(
                new TenantFijo(tenantActivo ?? Propietario), Autoridad(actor), _repositorio, new RelojFijo(Ahora))
            .Handle(new RegistrarEncargoAdministracionCommand(operacionId ?? _operacion.Id, clausula, vigenciaHasta), default);

    private Task<CaeManager.Domain.Common.Result> RetirarAsync(Actor actor, Guid encargoId, Guid? tenantActivo = null) =>
        new RetirarEncargoAdministracionCommandHandler(
                new TenantFijo(tenantActivo ?? Propietario), Autoridad(actor), _repositorio, new RelojFijo(Ahora))
            .Handle(new RetirarEncargoAdministracionCommand(encargoId), default);

    private EncargoAdministracion EncargoYaRegistrado()
    {
        var encargo = EncargoAdministracion.Registrar(
            _operacion, Clausula, EncargoAdministracion.VersionTextoVigente,
            OrigenEncargoAdministracion.AdministradorPropio, Guid.NewGuid(), Ahora.AddDays(-5), null);
        _repositorio.Encargos.Add(encargo);
        return encargo;
    }

    // --- Registrar -----------------------------------------------------------------------------

    [Fact]
    public async Task Un_Administrador_propio_del_Tenant_propietario_registra_el_encargo()
    {
        var administrador = AdministradorPropio();

        var resultado = await RegistrarAsync(administrador, vigenciaHasta: Ahora.AddYears(1));

        resultado.EsExitoso.Should().BeTrue();
        var encargo = _repositorio.Encargos.Should().ContainSingle().Subject;
        encargo.Id.Should().Be(resultado.Valor);
        encargo.PropietarioTenantId.Should().Be(Propietario);
        encargo.OperadorTenantId.Should().Be(Operador);
        encargo.AsignacionOperacionId.Should().Be(_operacion.Id);
        encargo.ClausulaContrato.Should().Be(Clausula);
        encargo.VersionTexto.Should().Be(EncargoAdministracion.VersionTextoVigente);
        encargo.Origen.Should().Be(OrigenEncargoAdministracion.AdministradorPropio);
        encargo.RegistradoPorUsuarioId.Should().Be(administrador.UsuarioId);
        encargo.RegistradoEnUtc.Should().Be(Ahora);
        encargo.VigenciaHasta.Should().Be(Ahora.AddYears(1));
    }

    [Fact]
    public async Task Soporte_TALVEG_lo_registra_en_una_Sesion_Privilegiada_de_aprovisionamiento_sobre_ese_Tenant()
    {
        var soporte = Soporte(CapacidadPrivilegio.Aprovisionamiento);

        (await RegistrarAsync(soporte)).EsExitoso.Should().BeTrue();

        var encargo = _repositorio.Encargos.Should().ContainSingle().Subject;
        encargo.Origen.Should().Be(OrigenEncargoAdministracion.AprovisionamientoDePlataforma);
        encargo.RegistradoPorUsuarioId.Should().Be(soporte.UsuarioId, "el Actor real es el técnico, no un usuario del Tenant");
    }

    [Theory]
    [InlineData(CapacidadPrivilegio.SoporteLectura)]
    [InlineData(CapacidadPrivilegio.AdminPlataforma)]
    [InlineData(CapacidadPrivilegio.BreakGlass)]
    public async Task Una_Sesion_Privilegiada_de_otra_capacidad_no_registra_y_no_cae_a_la_via_del_Administrador(
        CapacidadPrivilegio capacidad)
    {
        // La pertenencia contesta que sí a propósito: con una sesión abierta que no cumple, esa vía no se prueba.
        var soporte = Soporte(capacidad) with { EsAdministradorMiembro = true };

        var resultado = await RegistrarAsync(soporte);

        resultado.Error.Should().Be(AutoridadSobreElEncargo.NoAutorizado);
        _repositorio.Encargos.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_sesion_de_aprovisionamiento_sobre_otro_Tenant_no_registra_en_este()
    {
        var resultado = await RegistrarAsync(Soporte(CapacidadPrivilegio.Aprovisionamiento, tenantObjetivo: _otroTenant));

        resultado.Error.Should().Be(AutoridadSobreElEncargo.NoAutorizado);
        _repositorio.Encargos.Should().BeEmpty();
    }

    [Fact]
    public async Task El_Administrador_del_Operador_CAE_no_registra_su_propio_encargo_aunque_su_rol_efectivo_sea_Administrador()
    {
        var resultado = await RegistrarAsync(AdministradorDelOperadorCaePorEncargo());

        resultado.Error.Should().Be(AutoridadSobreElEncargo.NoAutorizado);
        _repositorio.Encargos.Should().BeEmpty();
        _repositorio.VecesQueSeLeyoUnaOperacion.Should().Be(0, "la autoridad va antes que cualquier lectura");
    }

    [Fact]
    public async Task Nadie_del_Operador_CAE_registra_aunque_el_predicado_de_pertenencia_fallara_abierto()
    {
        // Dos predicados distintos: si la pertenencia contestara que sí por un defecto, el rechazo
        // explícito del Tenant de origen sigue dejando fuera a quien recibe el encargo.
        var deLaCasaDelOperador = AdministradorDelOperadorCaePorEncargo() with { EsAdministradorMiembro = true };

        var resultado = await RegistrarAsync(deLaCasaDelOperador);

        resultado.Error.Should().Be(AutoridadSobreElEncargo.NoAutorizado);
        _repositorio.Encargos.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_Tenant_de_origen_resuelto_falla_cerrado()
    {
        var sinOrigen = AdministradorPropio() with { TenantOrigen = null };

        (await RegistrarAsync(sinOrigen)).Error.Should().Be(AutoridadSobreElEncargo.NoAutorizado);
        _repositorio.Encargos.Should().BeEmpty();
    }

    [Fact]
    public async Task Quien_solo_tiene_rol_efectivo_Administrador_sin_ser_miembro_no_registra()
    {
        // Ni miembro del Tenant propietario ni del Operador CAE del encargo: otro Operador CAE externo
        // del mismo Tenant, por ejemplo. Lo único que tiene es el rol efectivo, que aquí no decide.
        var tercero = new Actor(Guid.NewGuid(), _otroTenant, EsAdministradorMiembro: false);

        (await RegistrarAsync(tercero)).Error.Should().Be(AutoridadSobreElEncargo.NoAutorizado);
        _repositorio.Encargos.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_acto_simulado_no_registra_encargos()
    {
        var simulado = AdministradorPropio() with { UsuarioSimulado = Guid.NewGuid() };

        (await RegistrarAsync(simulado)).Error.Should().Be(AutoridadSobreElEncargo.NoAutorizado);
    }

    [Fact]
    public async Task Sin_Tenant_activo_no_hay_sobre_que_registrar()
    {
        var handler = new RegistrarEncargoAdministracionCommandHandler(
            new TenantFijo(null), Autoridad(AdministradorPropio()), _repositorio, new RelojFijo(Ahora));

        var resultado = await handler.Handle(new RegistrarEncargoAdministracionCommand(_operacion.Id, Clausula, null), default);

        resultado.Error.Should().Be(AutoridadSobreElEncargo.NoAutorizado);
    }

    [Fact]
    public async Task Solo_se_encarga_a_un_Operador_CAE_externo_que_opere_el_Tenant_entero_y_siga_vigente()
    {
        var raiz = AsignacionOperacion.Raiz(Propietario, ServicioCae.Outbound, Ahora.AddYears(-1), Ahora.AddYears(-1));
        var interna = AsignacionOperacion.Interna(
            Propietario, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(Guid.NewGuid()), Ahora.AddDays(-1), null, Ahora.AddDays(-1));
        var caducada = AsignacionOperacion.Externa(
            Propietario, Guid.NewGuid(), ServicioCae.Outbound, AmbitoAsignacion.Universal, Ahora.AddDays(-30), Ahora.AddDays(-1), Ahora.AddDays(-30));
        var deOtroPropietario = AsignacionOperacion.Externa(
            _otroTenant, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, Ahora.AddDays(-30), null, Ahora.AddDays(-30));
        _repositorio.Operaciones.AddRange([raiz, interna, caducada, deOtroPropietario]);

        foreach (var operacionId in new[] { raiz.Id, interna.Id, caducada.Id, deOtroPropietario.Id, Guid.NewGuid() })
            (await RegistrarAsync(AdministradorPropio(), operacionId)).Error
                .Should().Be(ErroresEncargoAdministracion.OperacionNoValida);

        _repositorio.Encargos.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Sin_clausula_no_hay_encargo(string clausula)
    {
        (await RegistrarAsync(AdministradorPropio(), clausula: clausula)).Error
            .Should().Be(ErroresEncargoAdministracion.ClausulaObligatoria);
        new RegistrarEncargoAdministracionCommandValidator()
            .Validate(new RegistrarEncargoAdministracionCommand(_operacion.Id, clausula, null)).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Una_clausula_mas_larga_que_el_limite_se_rechaza_en_el_validador_y_en_el_handler()
    {
        var larga = new string('x', EncargoAdministracion.LongitudMaximaClausula + 1);

        (await RegistrarAsync(AdministradorPropio(), clausula: larga)).Error
            .Should().Be(ErroresEncargoAdministracion.ClausulaDemasiadoLarga);
        new RegistrarEncargoAdministracionCommandValidator()
            .Validate(new RegistrarEncargoAdministracionCommand(_operacion.Id, larga, null)).IsValid.Should().BeFalse();
        new RegistrarEncargoAdministracionCommandValidator()
            .Validate(new RegistrarEncargoAdministracionCommand(_operacion.Id, larga[..^1], null)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Una_fecha_de_fin_que_no_es_futura_se_rechaza()
    {
        (await RegistrarAsync(AdministradorPropio(), vigenciaHasta: Ahora)).Error
            .Should().Be(ErroresEncargoAdministracion.VigenciaNoValida);
    }

    [Fact]
    public async Task Solo_hay_un_encargo_sin_retirar_por_operacion()
    {
        EncargoYaRegistrado();

        (await RegistrarAsync(AdministradorPropio())).Error.Should().Be(ErroresEncargoAdministracion.YaRegistrado);
        _repositorio.Encargos.Should().ContainSingle();
    }

    [Fact]
    public async Task Dos_registros_a_la_vez_el_que_pierde_la_carrera_recibe_que_ya_existe()
    {
        _repositorio.ElGuardadoPierdeLaCarrera = true;

        (await RegistrarAsync(AdministradorPropio())).Error.Should().Be(ErroresEncargoAdministracion.YaRegistrado);
    }

    [Fact]
    public async Task Tras_retirar_el_encargo_se_puede_registrar_otro()
    {
        var anterior = EncargoYaRegistrado();
        (await RetirarAsync(AdministradorPropio(), anterior.Id)).EsExitoso.Should().BeTrue();

        (await RegistrarAsync(AdministradorPropio())).EsExitoso.Should().BeTrue();

        _repositorio.Encargos.Should().HaveCount(2);
    }

    // --- Retirar -------------------------------------------------------------------------------

    [Fact]
    public async Task Un_Administrador_propio_retira_el_encargo_y_queda_quien_y_cuando()
    {
        var encargo = EncargoYaRegistrado();
        var administrador = AdministradorPropio();

        (await RetirarAsync(administrador, encargo.Id)).EsExitoso.Should().BeTrue();

        encargo.RetiradoPorUsuarioId.Should().Be(administrador.UsuarioId);
        encargo.RetiradoEnUtc.Should().Be(Ahora);
        encargo.EstaVigente(Ahora).Should().BeFalse();
        _repositorio.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Soporte_TALVEG_lo_retira_en_una_Sesion_Privilegiada_de_aprovisionamiento()
    {
        var encargo = EncargoYaRegistrado();

        (await RetirarAsync(Soporte(CapacidadPrivilegio.Aprovisionamiento), encargo.Id)).EsExitoso.Should().BeTrue();

        encargo.RetiradoEnUtc.Should().Be(Ahora);
    }

    [Fact]
    public async Task El_Administrador_del_Operador_CAE_no_retira_el_encargo_que_recibe_aunque_su_rol_efectivo_sea_Administrador()
    {
        var encargo = EncargoYaRegistrado();

        var resultado = await RetirarAsync(AdministradorDelOperadorCaePorEncargo(), encargo.Id);

        resultado.Error.Should().Be(AutoridadSobreElEncargo.NoAutorizado);
        encargo.RetiradoEnUtc.Should().BeNull();
        _repositorio.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Nadie_del_Operador_CAE_lo_retira_aunque_el_predicado_de_pertenencia_fallara_abierto()
    {
        var encargo = EncargoYaRegistrado();
        var deLaCasaDelOperador = AdministradorDelOperadorCaePorEncargo() with { EsAdministradorMiembro = true };

        (await RetirarAsync(deLaCasaDelOperador, encargo.Id)).Error.Should().Be(AutoridadSobreElEncargo.NoAutorizado);

        encargo.RetiradoEnUtc.Should().BeNull();
    }

    [Fact]
    public async Task Un_encargo_de_otro_Tenant_propietario_no_se_encuentra()
    {
        var encargo = EncargoYaRegistrado();
        var administradorDeOtroTenant = new Actor(Guid.NewGuid(), _otroTenant, EsAdministradorMiembro: true);

        (await RetirarAsync(administradorDeOtroTenant, encargo.Id, tenantActivo: _otroTenant)).Error
            .Should().Be(ErroresEncargoAdministracion.NoEncontrado);
        encargo.RetiradoEnUtc.Should().BeNull();
    }

    [Fact]
    public async Task Retirar_dos_veces_devuelve_que_ya_estaba_retirado_y_no_reescribe_quien_lo_retiro()
    {
        var encargo = EncargoYaRegistrado();
        var primero = AdministradorPropio();
        await RetirarAsync(primero, encargo.Id);

        (await RetirarAsync(AdministradorPropio(), encargo.Id)).Error.Should().Be(ErroresEncargoAdministracion.YaRetirado);

        encargo.RetiradoPorUsuarioId.Should().Be(primero.UsuarioId);
    }

    // --- Leer ----------------------------------------------------------------------------------

    [Fact]
    public async Task La_lista_de_encargos_la_ve_quien_puede_gobernarlos_con_su_estado()
    {
        var vigente = EncargoYaRegistrado();
        var tenants = new TenantsQueryContextFalso();
        tenants.ListaTenants.AddRange([_propietario, _operador]);

        var lista = await ListarAsync(AdministradorPropio(), tenants);

        var fila = lista.Should().ContainSingle().Subject;
        fila.Id.Should().Be(vigente.Id);
        fila.OperadorNombre.Should().Be(_operador.Nombre);
        fila.ClausulaContrato.Should().Be(Clausula);
        fila.Estado.Should().Be(EstadoEncargoAdministracion.Vigente);

        vigente.Retirar(Guid.NewGuid(), Ahora.AddMinutes(-1));
        (await ListarAsync(AdministradorPropio(), tenants)).Should().ContainSingle()
            .Which.Estado.Should().Be(EstadoEncargoAdministracion.Retirado);
    }

    [Fact]
    public async Task Quien_administra_por_encargo_no_ve_la_lista_de_encargos()
    {
        EncargoYaRegistrado();
        var tenants = new TenantsQueryContextFalso();
        tenants.ListaTenants.AddRange([_propietario, _operador]);

        (await ListarAsync(AdministradorDelOperadorCaePorEncargo(), tenants)).Should().BeEmpty();
    }

    private Task<IReadOnlyList<EncargoAdministracionDto>> ListarAsync(Actor actor, TenantsQueryContextFalso tenants) =>
        new ObtenerEncargosAdministracionQueryHandler(
                new TenantFijo(Propietario), Autoridad(actor), _repositorio, tenants, new RelojFijo(Ahora))
            .Handle(new ObtenerEncargosAdministracionQuery(), default);

    // --- Dobles --------------------------------------------------------------------------------

    private sealed class RepositorioFalso : IEncargoAdministracionRepository, IEncargosAdministracionQueryContext
    {
        public List<AsignacionOperacion> Operaciones { get; } = [];
        public List<EncargoAdministracion> Encargos { get; } = [];
        public bool ElGuardadoPierdeLaCarrera { get; set; }
        public int VecesGuardado { get; private set; }
        public int VecesQueSeLeyoUnaOperacion { get; private set; }

        public IQueryable<EncargoAdministracion> EncargosAdministracion =>
            new TestAsyncQueryable<EncargoAdministracion>(Encargos.AsQueryable());

        public Task<EncargoAdministracion?> ObtenerPorIdAsync(
            Guid id, Guid propietarioTenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Encargos.SingleOrDefault(e => e.Id == id && e.PropietarioTenantId == propietarioTenantId));

        public Task<AsignacionOperacion?> ObtenerOperacionAsync(
            Guid asignacionOperacionId, Guid propietarioTenantId, CancellationToken cancellationToken = default)
        {
            VecesQueSeLeyoUnaOperacion++;
            return Task.FromResult(Operaciones.SingleOrDefault(
                o => o.Id == asignacionOperacionId && o.PropietarioTenantId == propietarioTenantId));
        }

        public Task<bool> ExisteSinRetirarAsync(Guid asignacionOperacionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Encargos.Any(e => e.AsignacionOperacionId == asignacionOperacionId && e.RetiradoEnUtc is null));

        public void Agregar(EncargoAdministracion encargo)
        {
            if (!ElGuardadoPierdeLaCarrera) Encargos.Add(encargo);
        }

        public Task<bool> GuardarDetectandoCarreraAsync(CancellationToken cancellationToken = default)
        {
            VecesGuardado++;
            return Task.FromResult(!ElGuardadoPierdeLaCarrera);
        }
    }

    private sealed class SesionFija(SesionPrivilegiadaActiva? sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) => Task.FromResult(sesion);
        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) => Task.FromResult(sesion);
    }

    private sealed class ActorFijo(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }

    private sealed class PertenenciaFija(bool esAdministradorMiembro) : IAdministradorDelTenantPropietario
    {
        public Task<bool> EsAdministradorEnBaseAsync(Guid usuarioId, Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(esAdministradorMiembro);
    }

    private sealed class TenantFijo(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    private sealed class RelojFijo(DateTime ahoraUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(ahoraUtc, TimeSpan.Zero);
    }
}
