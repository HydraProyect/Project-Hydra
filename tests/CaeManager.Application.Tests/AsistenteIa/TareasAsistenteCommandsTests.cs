using CaeManager.Application.AsistenteIa.Tareas;
using CaeManager.Application.AsistenteIa.Tareas.Commands;
using CaeManager.Application.Common;
using CaeManager.Domain.AsistenteIa;
using CaeManager.Domain.Common;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.AsistenteIa;

/// <summary>
/// Capa de aplicación de la tarea del asistente: quién es la persona, en qué
/// Tenant trabaja y por qué vía, y que un Command nunca toca la tarea de otra
/// persona. Las transiciones las prueba el dominio; aquí se prueba que sus
/// rechazos llegan como <see cref="Result"/> sin guardar nada.
/// </summary>
public class TareasAsistenteCommandsTests
{
    private readonly Guid _persona = Guid.NewGuid();
    private readonly Guid _tenantPropio = Guid.NewGuid();
    private readonly Guid _tenantBeneficiario = Guid.NewGuid();
    private readonly RepositorioEnMemoria _repositorio = new();
    private readonly UnidadDeTrabajoContada _unidad = new();

    // ── Resolución de la persona ──────────────────────────────────────────

    [Fact]
    public async Task Una_persona_en_su_propio_Tenant_abre_una_tarea_con_via_normal()
    {
        var resultado = await Crear(ActorAuditoria.Normal(_persona), _tenantPropio, _tenantPropio);

        resultado.EsExitoso.Should().BeTrue();
        var tarea = _repositorio.Tareas.Single();
        tarea.ActorRealUsuarioId.Should().Be(_persona);
        tarea.TenantOrigenId.Should().Be(_tenantPropio);
        tarea.ViaAcceso.Should().Be(Domain.Auditoria.TipoViaAccesoAuditoria.Normal);
        tarea.Turnos.Single().TextoOriginal.Should().Be("Alta del Centro Norte");
        _unidad.Guardados.Should().Be(1);
    }

    [Fact]
    public async Task Un_Gestor_CAE_de_un_Operador_CAE_externo_abre_la_tarea_por_su_Asignacion_de_Operacion()
    {
        var asignacion = Guid.NewGuid();
        var actor = new ActorAuditoria(_persona, null, TipoViaAcceso.OperacionDelegada, asignacion);

        var resultado = await Crear(actor, tenantOrigen: _tenantPropio, tenantObjetivo: _tenantBeneficiario);

        resultado.EsExitoso.Should().BeTrue();
        var tarea = _repositorio.Tareas.Single();
        tarea.TenantOrigenId.Should().Be(_tenantPropio);
        tarea.ViaAcceso.Should().Be(Domain.Auditoria.TipoViaAccesoAuditoria.OperacionDelegada);
        tarea.ViaAccesoId.Should().Be(asignacion);
    }

    [Fact]
    public async Task Otro_Tenant_con_via_normal_no_basta_una_coordenada_de_contexto_no_es_autoridad()
    {
        var resultado = await Crear(ActorAuditoria.Normal(_persona), tenantOrigen: _tenantPropio, tenantObjetivo: _tenantBeneficiario);

        resultado.Error.Codigo.Should().Be("TareaAsistente.TenantSinOperacion");
        _repositorio.Tareas.Should().BeEmpty();
        _unidad.Guardados.Should().Be(0);
    }

    [Fact]
    public async Task Una_Sesion_Privilegiada_no_usa_el_asistente()
    {
        var actor = new ActorAuditoria(Guid.NewGuid(), _persona, TipoViaAcceso.SesionPrivilegiada, Guid.NewGuid());
        var usuario = new CurrentUserServiceFalso(actor.ActorRealUsuarioId, tenantOrigenId: _tenantPropio);

        var resultado = await CrearCon(actor, usuario, _tenantPropio);

        resultado.Error.Codigo.Should().Be("TareaAsistente.SesionPrivilegiada");
        _repositorio.Tareas.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_persona_resuelta_o_con_una_sesion_que_no_coincide_no_se_abre_nada()
    {
        (await Crear(ActorAuditoria.SinResolver, _tenantPropio, _tenantPropio)).Error.Codigo.Should().Be("TareaAsistente.SinPersona");

        var otra = new CurrentUserServiceFalso(Guid.NewGuid(), tenantOrigenId: _tenantPropio);
        (await CrearCon(ActorAuditoria.Normal(_persona), otra, _tenantPropio)).Error.Codigo.Should().Be("TareaAsistente.IdentidadIncoherente");

        (await Crear(ActorAuditoria.Normal(_persona), _tenantPropio, tenantObjetivo: null)).Error.Codigo.Should().Be("TareaAsistente.SinTenant");

        var delegadaSinAsignacion = new ActorAuditoria(_persona, null, TipoViaAcceso.OperacionDelegada, null);
        (await Crear(delegadaSinAsignacion, _tenantPropio, _tenantBeneficiario)).Error.Codigo.Should().Be("TareaAsistente.ViaDesconocida");

        _repositorio.Tareas.Should().BeEmpty();
        _unidad.Guardados.Should().Be(0);
    }

    // ── Modificación: solo la tarea propia ─────────────────────────────────

    [Fact]
    public async Task La_tarea_de_otra_persona_del_mismo_Tenant_se_trata_como_inexistente()
    {
        var ajena = new TareaAsistente(Guid.NewGuid(), null, _tenantPropio, Domain.Auditoria.TipoViaAccesoAuditoria.Normal, null, DateTime.UtcNow);
        _repositorio.Tareas.Add(ajena);

        var resultado = await new DescartarTareaAsistenteCommandHandler(Modificacion(ActorAuditoria.Normal(_persona), _tenantPropio))
            .Handle(new DescartarTareaAsistenteCommand(ajena.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("TareaAsistente.NoEncontrada");
        ajena.Estado.Should().Be(EstadoTareaAsistente.Conversando);
        _unidad.Guardados.Should().Be(0);
    }

    [Fact]
    public async Task Confirmar_con_una_version_vista_antigua_no_confirma_un_plan_que_la_persona_no_vio()
    {
        var tarea = TareaPropiaConPlanListo();
        var modificacion = Modificacion(ActorAuditoria.Normal(_persona), _tenantPropio);

        var resultado = await new ConfirmarPlanTareaAsistenteCommandHandler(modificacion)
            .Handle(new ConfirmarPlanTareaAsistenteCommand(tarea.Id, Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        tarea.Estado.Should().Be(EstadoTareaAsistente.PlanListo);
        tarea.PlanConfirmadoEnUtc.Should().BeNull();
        _unidad.Guardados.Should().Be(0);
    }

    [Fact]
    public async Task Confirmar_con_la_version_vista_registra_al_Actor_real_y_al_Usuario_simulado_por_separado()
    {
        var tarea = TareaPropiaConPlanListo();
        var simulado = Guid.NewGuid();
        var actor = new ActorAuditoria(_persona, simulado, TipoViaAcceso.Normal, null);

        var resultado = await new ConfirmarPlanTareaAsistenteCommandHandler(Modificacion(actor, _tenantPropio))
            .Handle(new ConfirmarPlanTareaAsistenteCommand(tarea.Id, tarea.Version), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        tarea.PlanConfirmadoPorActorRealUsuarioId.Should().Be(_persona);
        tarea.PlanConfirmadoComoUsuarioSimuladoId.Should().Be(simulado);
        tarea.Pasos.Single().Estado.Should().Be(EstadoPasoTareaAsistente.Confirmado);
        _unidad.Guardados.Should().Be(1);
    }

    [Fact]
    public async Task Un_rechazo_del_dominio_llega_como_resultado_y_no_se_guarda()
    {
        var tarea = new TareaAsistente(_persona, null, _tenantPropio, Domain.Auditoria.TipoViaAccesoAuditoria.Normal, null, DateTime.UtcNow);
        _repositorio.Tareas.Add(tarea);

        var resultado = await new ConfirmarPlanTareaAsistenteCommandHandler(Modificacion(ActorAuditoria.Normal(_persona), _tenantPropio))
            .Handle(new ConfirmarPlanTareaAsistenteCommand(tarea.Id, tarea.Version), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("TareaAsistente.TransicionNoValida");
        _unidad.Guardados.Should().Be(0);
    }

    [Fact]
    public void Confirmar_sin_version_vista_no_pasa_la_validacion()
    {
        new ConfirmarPlanTareaAsistenteCommandValidator()
            .Validate(new ConfirmarPlanTareaAsistenteCommand(Guid.NewGuid(), Guid.Empty))
            .IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("ninguna")]
    [InlineData("orden_que_no_existe")]
    public void Un_paso_del_plan_solo_nombra_una_orden_del_catalogo(string orden)
    {
        new PasoPlanTareaAsistenteDtoValidator()
            .Validate(new PasoPlanTareaAsistenteDto(orden, "{}", null, [], []))
            .IsValid.Should().BeFalse();
    }

    private TareaAsistente TareaPropiaConPlanListo()
    {
        var tarea = new TareaAsistente(_persona, null, _tenantPropio, Domain.Auditoria.TipoViaAccesoAuditoria.Normal, null, DateTime.UtcNow);
        tarea.GuardarPlan([new("alta_centro", """{"nombre":"Centro Norte"}""", null, [], [])], true, DateTime.UtcNow);
        _repositorio.Tareas.Add(tarea);
        return tarea;
    }

    private Task<Result<Guid>> Crear(ActorAuditoria actor, Guid? tenantOrigen, Guid? tenantObjetivo) =>
        CrearCon(actor, new CurrentUserServiceFalso(actor.ActorRealUsuarioId, tenantOrigenId: tenantOrigen), tenantObjetivo);

    private Task<Result<Guid>> CrearCon(ActorAuditoria actor, ICurrentUserService usuario, Guid? tenantObjetivo) =>
        new CrearTareaAsistenteCommandHandler(_repositorio, new ActorFijo(actor), usuario, new TenantFijo(tenantObjetivo), _unidad)
            .Handle(new CrearTareaAsistenteCommand("Alta del Centro Norte", null), CancellationToken.None);

    private ModificacionTareaAsistente Modificacion(ActorAuditoria actor, Guid tenant) =>
        new(_repositorio, new ActorFijo(actor), new CurrentUserServiceFalso(actor.ActorRealUsuarioId, tenantOrigenId: tenant), new TenantFijo(tenant), _unidad);

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
