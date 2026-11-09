using CaeManager.Application.AsistenteIa.Candidatos;
using CaeManager.Application.AsistenteIa.Edicion;
using CaeManager.Application.AsistenteIa.Ordenes;
using CaeManager.Application.AsistenteIa.Queries.ProponerPlan;
using CaeManager.Application.AsistenteIa.Tareas;
using CaeManager.Application.AsistenteIa.Tareas.Commands;
using CaeManager.Application.AsistenteIa.Tareas.Queries;
using CaeManager.Application.Asignaciones.Commands.CrearAsignacion;
using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores.Commands.CrearTrabajador;
using CaeManager.Domain.AsistenteIa;
using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Application.Tests.AsistenteIa;

/// <summary>
/// El plan de Vega se completa a mano, se confirma y se ejecuta por los Commands reales
/// del catálogo: alta de Trabajador y su Asignación a un Centro, de punta a punta. Lo que
/// se prueba es que el plan no guarda ni ejecuta nada fuera de la cartera de la persona,
/// que la ejecución solo ocurre en el Tenant efectivo y que reintentar no repite un paso.
/// </summary>
public class PlanEditableAsistenteTests
{
    private const string DniValido = "12345678Z";

    private readonly Guid _persona = Guid.NewGuid();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _empresa = Guid.NewGuid();
    private readonly Guid _subcontrata = Guid.NewGuid();
    private readonly Guid _cliente = Guid.NewGuid();
    private readonly Guid _otroCliente = Guid.NewGuid();
    private readonly Guid _centro = Guid.NewGuid();
    private readonly Guid _centroDeOtroCliente = Guid.NewGuid();
    private readonly RepositorioEnMemoria _repositorio = new();
    private readonly UnidadDeTrabajoContada _unidad = new();
    private readonly TrabajadoresFalsos _trabajadores = new();
    private readonly MediadorFalso _mediador;

    public PlanEditableAsistenteTests()
    {
        _mediador = new MediadorFalso(_repositorio) { Opciones = OpcionesDeCartera() };
    }

    private OpcionesPlanAsistenteDto OpcionesDeCartera() => new(
        new TenantDeCarteraDto(_tenant, "Contratas Norte", EsOrigen: false),
        new Dictionary<string, IReadOnlyList<OpcionDatoPlanDto>>(StringComparer.Ordinal)
        {
            [ObtenerCandidatosAsistenteQueryHandler.CampoEmpleador] =
            [
                new(_empresa, "Contratas Norte"),
                new(_subcontrata, "Montajes Sur (subcontrata)", EsSubcontrata: true),
            ],
            [ObtenerCandidatosAsistenteQueryHandler.CampoClienteEmpresarial] = [new(_cliente, "Química Levante"), new(_otroCliente, "Aceros Este")],
            [ObtenerCandidatosAsistenteQueryHandler.CampoCentro] =
            [
                new(_centro, "Planta Sagunto · Química Levante", GrupoId: _cliente),
                new(_centroDeOtroCliente, "Nave 2 · Aceros Este", GrupoId: _otroCliente),
            ],
        });

    // ── Completar ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Con_todos_los_datos_de_la_cartera_el_plan_queda_listo_con_un_paso_por_operacion()
    {
        var tarea = await TareaConPlanAsync();

        var resultado = await CompletarAsync(tarea, ValoresCompletos());

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : "");
        resultado.Valor.Plan.Confirmable.Should().BeTrue();
        resultado.Valor.Plan.Destino!.Tenant!.TenantId.Should().Be(_tenant);
        tarea.Estado.Should().Be(EstadoTareaAsistente.PlanListo);
        tarea.Pasos.Should().HaveCount(2);
        tarea.Pasos.Select(p => PlanPropuestoATareaAsistente.LeerOperacion(p.DatosJson))
            .Should().Equal(nameof(CrearTrabajadorCommand), nameof(CrearAsignacionCommand));
    }

    [Fact]
    public async Task Un_Centro_que_no_esta_entre_las_opciones_de_la_cartera_falla_cerrado_y_no_se_guarda()
    {
        var tarea = await TareaConPlanAsync();
        var guardadosAntes = _unidad.Guardados;
        var valores = ValoresCompletos()
            .Select(v => v.Campo == ObtenerCandidatosAsistenteQueryHandler.CampoCentro ? v with { Id = Guid.NewGuid() } : v)
            .ToList();

        var resultado = await CompletarAsync(tarea, valores);

        resultado.Error.Codigo.Should().Be(RevisionPlanAsistente.CodigoFueraDeCartera);
        _unidad.Guardados.Should().Be(guardadosAntes);
        tarea.Estado.Should().NotBe(EstadoTareaAsistente.PlanListo);
    }

    [Fact]
    public async Task Un_Gestor_CAE_cuyo_Tenant_efectivo_no_es_de_su_cartera_no_completa_nada()
    {
        var tarea = await TareaConPlanAsync();
        _mediador.Opciones = OpcionesPlanAsistenteDto.Vacias;

        var resultado = await CompletarAsync(tarea, ValoresCompletos());

        resultado.Error.Codigo.Should().Be(RevisionPlanAsistente.CodigoSinCartera);
    }

    [Fact]
    public async Task Un_Centro_de_otro_Cliente_empresarial_bloquea_el_plan()
    {
        var tarea = await TareaConPlanAsync();
        var valores = ValoresCompletos()
            .Select(v => v.Campo == ObtenerCandidatosAsistenteQueryHandler.CampoCentro ? v with { Id = _centroDeOtroCliente } : v)
            .ToList();

        var resultado = await CompletarAsync(tarea, valores);

        resultado.Valor.Plan.Confirmable.Should().BeFalse();
        resultado.Valor.Plan.Avisos.Should().Contain(a =>
            a.Gravedad == GravedadAvisoPasoTareaAsistente.Bloqueante && a.Campo == ObtenerCandidatosAsistenteQueryHandler.CampoCentro);
    }

    [Fact]
    public async Task Un_documento_ya_dado_de_alta_o_mal_escrito_bloquea_el_plan()
    {
        var tarea = await TareaConPlanAsync();
        _trabajadores.Existentes.Add(DniValido);

        var duplicado = await CompletarAsync(tarea, ValoresCompletos());
        var malEscrito = await CompletarAsync(tarea, ValoresCompletos(documento: "12345678A"));

        duplicado.Valor.Plan.Confirmable.Should().BeFalse();
        malEscrito.Valor.Plan.Confirmable.Should().BeFalse();
        malEscrito.Valor.Plan.Avisos.Should().Contain(a => a.Campo == TiposDatoPlan.CampoTrabajador);
    }

    [Fact]
    public async Task Falta_un_dato_obligatorio_y_el_plan_sigue_en_borrador()
    {
        var tarea = await TareaConPlanAsync();
        var valores = ValoresCompletos().Where(v => v.Campo != TiposDatoPlan.CampoFechaInicio).ToList();

        var resultado = await CompletarAsync(tarea, valores);

        resultado.Valor.Plan.Confirmable.Should().BeFalse();
        tarea.Estado.Should().Be(EstadoTareaAsistente.PlanEnBorrador);
    }

    // ── Ejecutar ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Al_ejecutar_se_da_de_alta_al_Trabajador_y_se_le_asigna_al_Centro_por_los_Commands_reales()
    {
        var tarea = await TareaConfirmadaAsync();

        var resultado = await EjecutarAsync(tarea);

        resultado.Valor.Terminada.Should().BeTrue();
        var alta = _mediador.Enviados.OfType<CrearTrabajadorCommand>().Single();
        alta.EmpresaId.Should().Be(_empresa);
        alta.SubcontrataId.Should().BeNull();
        (alta.Nombre, alta.Apellidos, alta.Dni).Should().Be(("Lucía", "Pérez Gil", DniValido));
        var asignacion = _mediador.Enviados.OfType<CrearAsignacionCommand>().Single();
        asignacion.TrabajadorId.Should().Be(_mediador.TrabajadorCreado);
        asignacion.CentroId.Should().Be(_centro);
        asignacion.FechaAlta.Should().Be(new DateOnly(2030, 3, 2));
        tarea.Estado.Should().Be(EstadoTareaAsistente.Terminada);
        tarea.Pasos[0].EntidadResultadoId.Should().Be(_mediador.TrabajadorCreado);
    }

    [Fact]
    public async Task Si_la_Asignacion_falla_reintentar_no_vuelve_a_dar_de_alta_al_Trabajador()
    {
        var tarea = await TareaConfirmadaAsync();
        _mediador.AsignacionFalla = true;

        var primero = await EjecutarAsync(tarea);

        primero.Valor.Terminada.Should().BeFalse();
        primero.Valor.Pasos.Select(p => p.Estado).Should().Equal(EstadoPasoTareaAsistente.Ejecutado, EstadoPasoTareaAsistente.Fallido);
        primero.Valor.Pasos[1].Motivo.Should().Be("El centro no es de tu cartera.");

        _mediador.AsignacionFalla = false;
        var segundo = await EjecutarAsync(tarea);

        segundo.Valor.Terminada.Should().BeTrue();
        _mediador.Enviados.OfType<CrearTrabajadorCommand>().Should().ContainSingle();
        _mediador.Enviados.OfType<CrearAsignacionCommand>().Should().HaveCount(2)
            .And.OnlyContain(a => a.TrabajadorId == _mediador.TrabajadorCreado);
    }

    [Fact]
    public async Task Con_otro_Tenant_efectivo_no_se_ejecuta_nada()
    {
        var tarea = await TareaConfirmadaAsync();

        var resultado = await EjecutarAsync(tarea, tenantEfectivo: Guid.NewGuid());

        resultado.Error.Codigo.Should().Be("AsistenteIa.DestinoNoEsLaPantalla");
        _mediador.Enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task Si_la_cartera_cambia_entre_confirmar_y_ejecutar_no_se_ejecuta_nada()
    {
        var tarea = await TareaConfirmadaAsync();
        _mediador.Opciones = OpcionesPlanAsistenteDto.Vacias;

        var resultado = await EjecutarAsync(tarea);

        resultado.Error.Codigo.Should().Be(RevisionPlanAsistente.CodigoSinCartera);
        _mediador.Enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_plan_sin_confirmar_no_se_ejecuta()
    {
        var tarea = await TareaConPlanAsync();
        await CompletarAsync(tarea, ValoresCompletos());

        var resultado = await EjecutarAsync(tarea);

        resultado.Error.Codigo.Should().Be("AsistenteIa.PlanSinConfirmar");
        _mediador.Enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_empleador_subcontrata_da_de_alta_al_Trabajador_en_la_Subcontrata()
    {
        var tarea = await TareaConfirmadaAsync(empleador: _subcontrata);

        await EjecutarAsync(tarea);

        var alta = _mediador.Enviados.OfType<CrearTrabajadorCommand>().Single();
        alta.SubcontrataId.Should().Be(_subcontrata);
        alta.EmpresaId.Should().BeNull();
    }

    // ── Catálogo ──────────────────────────────────────────────────────────

    [Fact]
    public void Vega_ejecuta_las_altas_y_la_Visita_pero_no_las_ordenes_que_mandan_correo_o_solo_consultan()
    {
        EjecucionPlanAsistente.SabeEjecutar(CatalogoOrdenesAsistente.AltaTrabajadorYAsignacion).Should().BeTrue();
        EjecucionPlanAsistente.SabeEjecutar(CatalogoOrdenesAsistente.VisitaPuntualACentro).Should().BeTrue();
        EjecucionPlanAsistente.SabeEjecutar(CatalogoOrdenesAsistente.AltaCentro).Should().BeTrue();
        EjecucionPlanAsistente.SabeEjecutar(CatalogoOrdenesAsistente.AltaClienteEmpresarial).Should().BeTrue();
        EjecucionPlanAsistente.SabeEjecutar(CatalogoOrdenesAsistente.ReclamarDocumentacion).Should().BeFalse();
        EjecucionPlanAsistente.SabeEjecutar(CatalogoOrdenesAsistente.ConsultaDeEstado).Should().BeFalse();
    }

    [Fact]
    public void La_nota_que_ve_el_Gestor_CAE_en_una_orden_ejecutable_no_nombra_operaciones_internas()
    {
        foreach (var orden in CatalogoOrdenesAsistente.Ordenes.Where(o => EjecucionPlanAsistente.SabeEjecutar(o.Id)))
            orden.Limitacion.Should().NotContain("Command", "la nota de {0} se enseña en el plan, en lenguaje de producto", orden.Id);
    }

    // ── Andamiaje ─────────────────────────────────────────────────────────

    private List<ValorDatoPlanDto> ValoresCompletos(string documento = DniValido, Guid? empleador = null) =>
    [
        new(TiposDatoPlan.CampoTrabajador, Trabajador: new TrabajadorNuevoDto("Lucía", "Pérez Gil", documento)),
        new(ObtenerCandidatosAsistenteQueryHandler.CampoEmpleador, Id: empleador ?? _empresa),
        new(ObtenerCandidatosAsistenteQueryHandler.CampoClienteEmpresarial, Id: _cliente),
        new(ObtenerCandidatosAsistenteQueryHandler.CampoCentro, Id: _centro),
        new(TiposDatoPlan.CampoFechaInicio, Fecha: new DateOnly(2030, 3, 2)),
        new(TiposDatoPlan.CampoMotivo, Texto: "Mantenimiento de la caldera"),
    ];

    private async Task<TareaAsistente> TareaConPlanAsync()
    {
        var actor = ActorAuditoria.Normal(_persona);
        var creada = await new CrearTareaAsistenteCommandHandler(
                _repositorio, new ActorFijo(actor), Usuario(), new TenantFijo(_tenant), _unidad)
            .Handle(new CrearTareaAsistenteCommand("Da de alta a Lucía en Planta Sagunto", null), CancellationToken.None);
        creada.EsExitoso.Should().BeTrue();

        var plan = new PlanPropuestoDto(
            SituacionPlan.Propuesto, CatalogoOrdenesAsistente.AltaTrabajadorYAsignacion, 90,
            CatalogoOrdenesAsistente.PorId(CatalogoOrdenesAsistente.AltaTrabajadorYAsignacion)!.Campos
                .Select(c => new DatoPropuestoDto(c.Nombre, c.Descripcion, c.Obligatorio, null, null, null, 0, null))
                .ToList(),
            new TenantDestinoDto(SituacionTenantDestino.Elegir, null, [], "Elige"),
            Ejecutable: true, Limitacion: null);
        var guardado = await new GuardarPlanTareaAsistenteCommandHandler(Modificacion())
            .Handle(new GuardarPlanTareaAsistenteCommand(creada.Valor, PlanPropuestoATareaAsistente.APasos(plan), true), CancellationToken.None);
        guardado.EsExitoso.Should().BeTrue();
        return _repositorio.Tareas.Single(t => t.Id == creada.Valor);
    }

    private async Task<TareaAsistente> TareaConfirmadaAsync(Guid? empleador = null)
    {
        var tarea = await TareaConPlanAsync();
        var completado = await CompletarAsync(tarea, ValoresCompletos(empleador: empleador));
        completado.Valor.Plan.Confirmable.Should().BeTrue();
        tarea.ConfirmarPlan(_persona, null, DateTime.UtcNow);
        return tarea;
    }

    private Task<Result<PlanCompletadoDto>> CompletarAsync(TareaAsistente tarea, IReadOnlyList<ValorDatoPlanDto> valores) =>
        new CompletarPlanTareaAsistenteCommandHandler(_mediador, new RevisionPlanAsistente(_mediador, _trabajadores), Modificacion())
            .Handle(new CompletarPlanTareaAsistenteCommand(tarea.Id, tarea.Version, 90, valores), CancellationToken.None);

    private Task<Result<EjecucionPlanAsistenteDto>> EjecutarAsync(TareaAsistente tarea, Guid? tenantEfectivo = null) =>
        new EjecutarPlanTareaAsistenteCommandHandler(
                _mediador, new RevisionPlanAsistente(_mediador, _trabajadores), Modificacion(), new TenantFijo(tenantEfectivo ?? _tenant))
            .Handle(new EjecutarPlanTareaAsistenteCommand(tarea.Id), CancellationToken.None);

    private CurrentUserServiceFalso Usuario() => new(_persona, tenantOrigenId: _tenant);

    private ModificacionTareaAsistente Modificacion() =>
        new(_repositorio, new ActorFijo(ActorAuditoria.Normal(_persona)), Usuario(), new TenantFijo(_tenant), _unidad);

    /// <summary>
    /// Hace de MediatR: devuelve la tarea de la persona desde el repositorio en memoria,
    /// las opciones de la cartera que fije el test y registra los Commands reales enviados.
    /// </summary>
    private sealed class MediadorFalso(RepositorioEnMemoria repositorio) : IMediator
    {
        public OpcionesPlanAsistenteDto Opciones { get; set; } = OpcionesPlanAsistenteDto.Vacias;
        public bool AsignacionFalla { get; set; }
        public Guid TrabajadorCreado { get; } = Guid.NewGuid();
        public List<object> Enviados { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object respuesta = request switch
            {
                ObtenerTareaAsistenteQuery q => Tarea(q.TareaId),
                ObtenerOpcionesPlanAsistenteQuery => Opciones,
                CrearTrabajadorCommand alta => Registrar(alta, Result.Exito(TrabajadorCreado)),
                CrearAsignacionCommand asignacion => Registrar(asignacion, AsignacionFalla
                    ? Result.Fallo<Guid>(Error.Crear("Asignacion.CentroFueraDeCartera", "El centro no es de tu cartera."))
                    : Result.Exito(Guid.NewGuid())),
                _ => throw new NotSupportedException(request.GetType().Name),
            };
            return Task.FromResult((TResponse)respuesta);
        }

        private Result<Guid> Registrar(object comando, Result<Guid> resultado)
        {
            Enviados.Add(comando);
            return resultado;
        }

        private Result<TareaAsistenteDto> Tarea(Guid id)
        {
            var t = repositorio.Tareas.Single(x => x.Id == id);
            return Result.Exito(new TareaAsistenteDto(
                t.Id, t.Version, t.Estado, t.CreadaEnUtc, t.ActualizadaEnUtc, t.PlanConfirmadoEnUtc, [],
                t.Pasos.Select(p => new PasoTareaAsistenteDto(
                    p.Id, p.Posicion, p.OrdenAsistenteId, p.DatosJson, p.Resumen, p.CamposPendientes, p.Avisos,
                    p.AsistidoPorIa, p.Estado, p.MotivoFallo, p.EntidadResultadoId)).ToList()));
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class TrabajadoresFalsos : ITrabajadorRepository
    {
        public HashSet<string> Existentes { get; } = new(StringComparer.Ordinal);

        public Task<bool> ExisteConDniAsync(string dni, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Existentes.Contains(dni));

        public Task<Trabajador?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlySet<string>> ObtenerDnisEnPapeleraAsync(IReadOnlyCollection<string> dnis, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Agregar(Trabajador trabajador) => throw new NotSupportedException();
    }

    private sealed class RepositorioEnMemoria : ITareaAsistenteRepository
    {
        public List<TareaAsistente> Tareas { get; } = [];

        public void Agregar(TareaAsistente tarea) => Tareas.Add(tarea);

        public Task<TareaAsistente?> ObtenerDePersonaAsync(Guid tareaId, Guid actorRealUsuarioId, CancellationToken cancellationToken) =>
            Task.FromResult(Tareas.FirstOrDefault(t => t.Id == tareaId && t.ActorRealUsuarioId == actorRealUsuarioId));
    }

    private sealed class UnidadDeTrabajoContada : IUnitOfWork
    {
        public int Guardados { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Guardados++;
            return Task.FromResult(1);
        }
    }

    private sealed class ActorFijo(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }

    private sealed class TenantFijo(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }
}
