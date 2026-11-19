using CaeManager.Application.Common;
using CaeManager.Application.Tests.Asignaciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Comunicaciones;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Gestiones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Visitas;
using CaeManager.Application.Visitas.AltaDeVisita;
using CaeManager.Application.Visitas.Antelacion;
using CaeManager.Application.Visitas.Commands.CrearVisita;
using CaeManager.Application.Visitas.PaqueteDocumental;
using CaeManager.Application.Visitas.Queries.ObtenerPlanAltaVisita;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Visitas;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using CurrentUserServiceFalso = CaeManager.Application.Tests.CurrentUserServiceFalso;

namespace CaeManager.Application.Tests.Visitas;

/// <summary>
/// Crear una Visita con <c>AsignarYCrearGestiones</c> (decisión de Chris, 2026-11-19): en un solo guardado, la Visita, la
/// Asignación Trabajador–Centro de quien no la tenía y las Gestiones del plan. La seguridad no se relaja: el Centro dentro
/// de la Asignación de Cartera, los Trabajadores admisibles (Empresa del Centro o Subcontrata vigente, dentro del Tenant), la
/// autoridad para asignar y todo releído bajo el candado. Cualquier rechazo deja la Visita sin crear.
/// </summary>
public class CrearVisitaQueAsignaYCreaGestionesTests
{
    private static readonly DateOnly Inicio = new(2026, 12, 1);
    private static readonly DateOnly Fin = new(2026, 12, 10);

    private readonly Guid _clienteDeMiCartera = Guid.NewGuid();
    private readonly Centro _centro;
    private readonly Trabajador _asignada;
    private readonly Trabajador _sinAsignar;
    private readonly Trabajador _deOtraEmpresa;
    private readonly Guid _tipoFormacion = Guid.NewGuid();

    private readonly CentrosQueryContextFalso _centros = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly VisitaRepositorioFalso _visitas = new();
    private readonly VisitaTrabajadoresFalsos _visitaTrabajadores = new();
    private readonly AsignacionRepositorioFalso _asignaciones = new();
    private readonly GestionRepositorioFalso _gestiones = new();
    private readonly AltaAcreditacionesPlataformaServiceFalso _altaAcreditaciones = new();
    private readonly SerializacionAltasAcreditacionEmpresaFalsa _serializacion = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();

    public CrearVisitaQueAsignaYCreaGestionesTests()
    {
        _centro = new Centro(_clienteDeMiCartera, Guid.NewGuid(), "Planta norte");
        _asignada = Trabajador.DeEmpresa(_centro.EmpresaId, "Ana", "García", "12345678Z");
        _sinAsignar = Trabajador.DeEmpresa(_centro.EmpresaId, "Bruno", "Pérez", "87654321X");
        _deOtraEmpresa = Trabajador.DeEmpresa(Guid.NewGuid(), "Carla", "Ajena", "11223344B");
        _centros.ListaCentros.Add(_centro);
        _trabajadores.ListaTrabajadores.AddRange([_asignada, _sinAsignar, _deOtraEmpresa]);
        _asignaciones.Agregar(new Asignacion(_asignada.Id, _centro.Id, new DateOnly(2026, 1, 1)));
    }

    private AlcanceDatosServiceFalso AlcanceDeMiCartera() =>
        new(tieneAccesoTotal: false, clienteIdsVisibles: [_clienteDeMiCartera], centroIdsVisibles: [_centro.Id]);

    private PlanFalso Plan() => new(_asignaciones, _trabajadores.ListaTrabajadores, _tipoFormacion);

    private CrearVisitaCommandHandler Handler(
        IAlcanceDatosService? alcance = null, IAutoridadAsignacionesService? autoridad = null, IInvalidadorAlcance? invalidador = null,
        bool sinServicioDeAlta = false)
    {
        alcance ??= AlcanceDeMiCartera();
        var alta = new AltaDeVisitaConAsignaciones(
            Plan(), autoridad ?? new AutoridadFalsa(), _asignaciones, _gestiones, _altaAcreditaciones, _serializacion,
            invalidador ?? new InvalidadorQueNoHaceNada(), _unitOfWork);

        return new CrearVisitaCommandHandler(
            _visitas, _visitaTrabajadores, _centros, _trabajadores, _empresas, new SugerenciaVisitaCorreoRepositorioFalso(),
            new ComunicacionesQueryContextFalso(), new PaqueteQueNoEnvia(), new EvaluadorQueNoSella(),
            new CurrentUserServiceFalso(Guid.NewGuid()), new PublisherFalso(), _unitOfWork,
            NullLogger<CrearVisitaCommandHandler>.Instance, alcance, actorAuditoria: null,
            altaConAsignaciones: sinServicioDeAlta ? null : alta);
    }

    private CrearVisitaCommand Crear(IReadOnlyList<Guid> trabajadorIds, bool asignar = true) =>
        new(_centro.Id, Inicio, Fin, trabajadorIds, Notas: null, AsignarYCrearGestiones: asignar);

    private void NadaCreado()
    {
        _visitas.Visitas.Should().BeEmpty();
        _visitaTrabajadores.Agregadas.Should().BeEmpty();
        _asignaciones.Asignaciones.Should().ContainSingle("solo queda la Asignación sembrada de Ana");
        _gestiones.Gestiones.Should().BeEmpty();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Asigna_al_Trabajador_sin_Asignacion_y_crea_las_Gestiones_del_plan_en_un_solo_guardado()
    {
        var resultado = await Handler().Handle(Crear([_asignada.Id, _sinAsignar.Id]), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : "");
        _visitas.Visitas.Should().ContainSingle().Which.Id.Should().Be(resultado.Valor);
        _visitaTrabajadores.Agregadas.Select(v => v.TrabajadorId).Should().BeEquivalentTo([_asignada.Id, _sinAsignar.Id]);

        var nueva = _asignaciones.Asignaciones.Should().ContainSingle(a => a.TrabajadorId == _sinAsignar.Id).Subject;
        nueva.CentroId.Should().Be(_centro.Id);
        nueva.FechaBaja.Should().BeNull();
        _asignaciones.Asignaciones.Count(a => a.TrabajadorId == _asignada.Id).Should().Be(1, "Ana ya estaba asignada");
        _altaAcreditaciones.Recibidas.Should().ContainSingle().Which.Asignaciones.Should().Equal(nueva);

        _gestiones.Gestiones.Select(g => (g.TrabajadorId, g.CentroId, g.TipoDocumentoId)).Should().BeEquivalentTo(
            [(_asignada.Id, _centro.Id, _tipoFormacion), (_sinAsignar.Id, _centro.Id, _tipoFormacion)]);
        _serializacion.Llamadas.Should().Be(1);
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Sin_AsignarYCrearGestiones_crea_la_Visita_como_antes_sin_asignar_ni_crear_Gestiones()
    {
        var resultado = await Handler().Handle(Crear([_sinAsignar.Id], asignar: false), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        _visitas.Visitas.Should().ContainSingle();
        _asignaciones.Asignaciones.Should().ContainSingle();
        _gestiones.Gestiones.Should().BeEmpty();
        _serializacion.Llamadas.Should().Be(0);
    }

    [Fact]
    public async Task Un_usuario_fuera_de_la_cartera_del_Centro_no_crea_nada_y_se_le_responde_como_si_no_existiera()
    {
        var fueraDeCartera = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [Guid.NewGuid()]);

        var resultado = await Handler(alcance: fueraDeCartera).Handle(Crear([_sinAsignar.Id]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Visita.CentroNoEncontrado");
        NadaCreado();
    }

    [Fact]
    public async Task Un_Trabajador_de_otra_Empresa_no_se_asigna_y_la_Visita_no_se_crea()
    {
        var resultado = await Handler().Handle(Crear([_sinAsignar.Id, _deOtraEmpresa.Id]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Should().Be(TrabajadoresAdmisiblesEnVisita.NoAdmisible(varios: true));
        NadaCreado();
    }

    [Fact]
    public async Task Un_Trabajador_de_otro_Tenant_no_se_asigna_y_la_Visita_no_se_crea()
    {
        // Otro Tenant: el filtro de consulta y la RLS no lo devuelven, así que para este Tenant su Id no existe.
        var deOtroTenant = Guid.NewGuid();

        var resultado = await Handler().Handle(Crear([_sinAsignar.Id, deOtroTenant]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        NadaCreado();
    }

    [Fact]
    public async Task Si_la_Asignacion_de_Cartera_se_revoca_mientras_espera_el_candado_no_crea_nada()
    {
        var alcance = new AlcanceMemoizadoFalso(AlcanceDeMiCartera());
        _serializacion.AlTomarElCandado = alcance.Revocar;

        var resultado = await Handler(alcance: alcance, invalidador: alcance).Handle(Crear([_sinAsignar.Id]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        alcance.Invalidaciones.Should().BeGreaterThan(0);
        NadaCreado();
    }

    [Fact]
    public async Task Sin_autoridad_sobre_el_Trabajador_que_hay_que_asignar_rechaza_la_Visita()
    {
        var resultado = await Handler(autoridad: new AutoridadFalsa { SobreTrabajador = false })
            .Handle(Crear([_sinAsignar.Id]), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(AltaDeVisitaConAsignaciones.CodigoSinAutoridad);
        NadaCreado();
    }

    [Fact]
    public async Task Sin_autoridad_sobre_el_Centro_rechaza_la_Visita()
    {
        var resultado = await Handler(autoridad: new AutoridadFalsa { SobreCentro = false })
            .Handle(Crear([_sinAsignar.Id]), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(AltaDeVisitaConAsignaciones.CodigoSinAutoridad);
        NadaCreado();
    }

    [Fact]
    public async Task Sin_nadie_que_asignar_no_pide_autoridad_para_asignar()
    {
        var resultado = await Handler(autoridad: new AutoridadFalsa { SobreCentro = false, SobreTrabajador = false })
            .Handle(Crear([_asignada.Id]), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        _gestiones.Gestiones.Should().ContainSingle();
    }

    [Fact]
    public async Task Una_Asignacion_cerrada_que_se_solapa_con_hoy_rechaza_la_Visita()
    {
        var cerrada = new Asignacion(_sinAsignar.Id, _centro.Id, DiaDeNegocio.Hoy().AddDays(-5));
        cerrada.DarDeBaja(DiaDeNegocio.Hoy().AddDays(1));
        _asignaciones.Agregar(cerrada);

        var resultado = await Handler().Handle(Crear([_sinAsignar.Id]), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(AltaDeVisitaConAsignaciones.CodigoSolape);
        _visitas.Visitas.Should().BeEmpty();
        _gestiones.Gestiones.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_el_servicio_de_alta_una_peticion_que_asigna_falla_cerrada()
    {
        var resultado = await Handler(sinServicioDeAlta: true).Handle(Crear([_sinAsignar.Id]), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(AltaDeVisitaConAsignaciones.CodigoSinAutoridad);
        NadaCreado();
    }

    // ---- Vista previa: las mismas comprobaciones, sin escribir nada

    private ObtenerPlanAltaVisitaQueryHandler Vista(IAlcanceDatosService alcance, IAutoridadAsignacionesService? autoridad = null) =>
        new(_centros, _trabajadores, _empresas, alcance, autoridad ?? new AutoridadFalsa(), Plan());

    [Fact]
    public async Task La_vista_previa_ensena_el_mismo_plan_que_ejecuta_el_Command()
    {
        var previa = await Vista(AlcanceDeMiCartera())
            .Handle(new ObtenerPlanAltaVisitaQuery(_centro.Id, Fin, [_asignada.Id, _sinAsignar.Id]), CancellationToken.None);

        previa.Should().NotBeNull();
        previa!.PuedeAsignar.Should().BeTrue();
        previa.Plan.Asignaciones.Select(a => a.TrabajadorId).Should().Equal(_sinAsignar.Id);
        previa.Plan.Gestiones.Should().HaveCount(2);
        _gestiones.Gestiones.Should().BeEmpty();
        _asignaciones.Asignaciones.Should().ContainSingle();
    }

    [Fact]
    public async Task La_vista_previa_fuera_de_cartera_o_con_un_Trabajador_ajeno_no_devuelve_nada()
    {
        var fueraDeCartera = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [Guid.NewGuid()]);

        (await Vista(fueraDeCartera).Handle(new ObtenerPlanAltaVisitaQuery(_centro.Id, Fin, [_sinAsignar.Id]), CancellationToken.None))
            .Should().BeNull();
        (await Vista(AlcanceDeMiCartera()).Handle(new ObtenerPlanAltaVisitaQuery(_centro.Id, Fin, [_deOtraEmpresa.Id]), CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task La_vista_previa_avisa_cuando_falta_autoridad_para_asignar()
    {
        var previa = await Vista(AlcanceDeMiCartera(), new AutoridadFalsa { SobreTrabajador = false })
            .Handle(new ObtenerPlanAltaVisitaQuery(_centro.Id, Fin, [_sinAsignar.Id]), CancellationToken.None);

        previa!.PuedeAsignar.Should().BeFalse();
    }

    // ---- Falsos

    /// <summary>Asigna a quien no tiene Asignación activa y pide un único Tipo a todos: la regla en sí la prueba ReglaGestionesDeVisitaTests.</summary>
    private sealed class PlanFalso(AsignacionRepositorioFalso asignaciones, List<Trabajador> trabajadores, Guid tipoId) : IPlanAltaVisitaService
    {
        public Task<PlanAltaVisitaDto> CalcularAsync(
            Guid centroId, DateOnly fechaFin, IReadOnlyCollection<Guid> trabajadorIds, CancellationToken cancellationToken)
        {
            var deLaVisita = trabajadores.Where(t => trabajadorIds.Contains(t.Id)).ToList();
            return Task.FromResult(new PlanAltaVisitaDto(
                deLaVisita
                    .Where(t => !asignaciones.Asignaciones.Any(a => a.TrabajadorId == t.Id && a.CentroId == centroId && a.FechaBaja is null))
                    .Select(t => new AsignacionPrevistaDto(t.Id, t.Nombre))
                    .ToList(),
                deLaVisita.Select(t => new GestionPrevistaDto(t.Id, t.Nombre, tipoId, "Formación PRL", MotivoGestionPrevista.Falta)).ToList()));
        }
    }

    private sealed class AutoridadFalsa : IAutoridadAsignacionesService
    {
        public bool SobreCentro { get; init; } = true;
        public bool SobreTrabajador { get; init; } = true;

        public Task<bool> PuedeModificarAsignacionesDelCentroAsync(Guid centroId, CancellationToken cancellationToken = default) =>
            Task.FromResult(SobreCentro);

        public Task<IReadOnlyList<Guid>> FiltrarCentrosConAutoridadAsync(
            IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken = default) =>
            Task.FromResult(SobreCentro ? centroIds : (IReadOnlyList<Guid>)[]);

        public Task<bool> PuedeModificarAsignacionesDelTrabajadorAsync(Guid trabajadorId, CancellationToken cancellationToken = default) =>
            Task.FromResult(SobreTrabajador);

        public Task<IReadOnlyList<Guid>> FiltrarTrabajadoresConAutoridadAsync(
            IReadOnlyList<Guid> trabajadorIds, CancellationToken cancellationToken = default) =>
            Task.FromResult(SobreTrabajador ? trabajadorIds : (IReadOnlyList<Guid>)[]);
    }

    private sealed class InvalidadorQueNoHaceNada : IInvalidadorAlcance
    {
        public void Invalidar() { }
    }

    private sealed class PaqueteQueNoEnvia : IPaqueteDocumentalVisitaService
    {
        public Task GenerarYEnviarAsync(Guid visitaId, Guid conversacionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<PaqueteDocumentalZip?> ConstruirAsync(Guid visitaId, CancellationToken cancellationToken = default) =>
            Task.FromResult<PaqueteDocumentalZip?>(null);
    }

    private sealed class EvaluadorQueNoSella : IEvaluadorExpedienteVisitaService
    {
        public Task<bool> EvaluarAsync(Guid visitaId, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task EvaluarPorDocumentoAsync(Guid documentoId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class VisitaTrabajadoresFalsos : IVisitaTrabajadorRepository
    {
        public List<VisitaTrabajador> Agregadas { get; } = [];

        public Task<IReadOnlyList<VisitaTrabajador>> ObtenerPorVisitaAsync(Guid visitaId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VisitaTrabajador>>([]);

        public void Agregar(VisitaTrabajador visitaTrabajador) => Agregadas.Add(visitaTrabajador);

        public void Eliminar(VisitaTrabajador visitaTrabajador) { }
    }
}
