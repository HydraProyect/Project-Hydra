using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Common;

namespace CaeManager.Domain.AsistenteIa;

/// <summary>
/// Tarea del asistente de flujos: la conversación y el plan de una orden,
/// guardados en el servidor para que sobrevivan a la navegación, con los pasos
/// incompletos como borrador reanudable (propuesta del asistente, § 3, pasos 6
/// y 8).
///
/// <para>
/// <b>Propiedad.</b> Pertenece a un Tenant propietario (<see cref="EntidadConTenant.TenantId"/>,
/// sellado al guardar: el Tenant en el que se trabaja, que para un Gestor CAE
/// de un Operador CAE externo es el Tenant beneficiario de su Workspace
/// operativo derivado) y a <b>una persona</b>, <see cref="ActorRealUsuarioId"/>.
/// Nadie más la ve: ni otra persona del mismo Tenant ni Soporte TALVEG. RLS lo
/// sostiene en la base, no solo el filtro de la consulta.
/// </para>
///
/// <para>
/// <b>Nada se ejecuta sin confirmación.</b> El plan se confirma entero
/// (<see cref="ConfirmarPlan"/>) y solo después se puede registrar la ejecución
/// de un paso; tras confirmar, el plan ya no cambia. Los estados de los pasos
/// solo se mueven desde aquí.
/// </para>
///
/// <para>
/// <b>Actor real y Usuario simulado.</b> Se guardan por separado, tanto para
/// quien creó la tarea como para quien confirmó el plan. El Usuario simulado
/// va en <c>null</c> mientras no exista la impersonación (ADR-011 § 8.2).
/// </para>
/// </summary>
public class TareaAsistente : EntidadConTenant, IVersionable
{
    public const int MaximoTurnos = 200;
    public const int MaximoPasos = 20;

    private readonly List<TurnoTareaAsistente> _turnos = [];
    private readonly List<PasoTareaAsistente> _pasos = [];

    /// <inheritdoc cref="IVersionable" />
    public Guid Version { get; private set; } = Guid.NewGuid();

    /// <summary>La persona propietaria de la tarea: el Actor real que la creó.</summary>
    public Guid ActorRealUsuarioId { get; private set; }

    /// <summary>A quién simulaba el Actor real al crearla. Hoy siempre <c>null</c>.</summary>
    public Guid? UsuarioSimuladoId { get; private set; }

    /// <summary>
    /// Tenant de origen de la persona (al que pertenece su cuenta). Distinto de
    /// <see cref="EntidadConTenant.TenantId"/> cuando trabaja en un Workspace
    /// operativo derivado. Es evidencia, no autoridad.
    /// </summary>
    public Guid? TenantOrigenId { get; private set; }

    /// <summary>Vía por la que la persona operaba el Tenant al crear la tarea.</summary>
    public TipoViaAccesoAuditoria ViaAcceso { get; private set; }

    /// <summary>La Asignación de Operación de la vía delegada, si es esa la vía.</summary>
    public Guid? ViaAccesoId { get; private set; }

    public EstadoTareaAsistente Estado { get; private set; }

    public DateTime CreadaEnUtc { get; private set; }

    /// <summary>
    /// Se mueve con cualquier cambio del agregado, también de un turno o de un
    /// paso: así la raíz queda siempre modificada y <see cref="Version"/> protege
    /// la confirmación contra un cambio concurrente del plan.
    /// </summary>
    public DateTime ActualizadaEnUtc { get; private set; }

    public Guid? PlanConfirmadoPorActorRealUsuarioId { get; private set; }
    public Guid? PlanConfirmadoComoUsuarioSimuladoId { get; private set; }
    public DateTime? PlanConfirmadoEnUtc { get; private set; }

    public DateTime? DescartadaEnUtc { get; private set; }

    public IReadOnlyList<TurnoTareaAsistente> Turnos => _turnos.AsReadOnly();

    /// <summary>Los pasos en su orden de plan.</summary>
    public IReadOnlyList<PasoTareaAsistente> Pasos => _pasos.OrderBy(p => p.Posicion).ToList().AsReadOnly();

    public bool EstaCerrada => Estado is EstadoTareaAsistente.Terminada or EstadoTareaAsistente.Descartada;

    private TareaAsistente()
    {
    }

    public TareaAsistente(
        Guid actorRealUsuarioId,
        Guid? usuarioSimuladoId,
        Guid? tenantOrigenId,
        TipoViaAccesoAuditoria viaAcceso,
        Guid? viaAccesoId,
        DateTime ahoraUtc)
    {
        if (actorRealUsuarioId == Guid.Empty)
            throw new ArgumentException("La tarea debe pertenecer a una persona.", nameof(actorRealUsuarioId));
        if (usuarioSimuladoId == Guid.Empty)
            throw new ArgumentException("El Usuario simulado, si existe, debe ser un usuario.", nameof(usuarioSimuladoId));

        // Soporte TALVEG nunca es Gestor CAE: una Sesión Privilegiada no abre
        // tareas del asistente, que son trabajo operativo del Tenant. Y una vía
        // sin resolver no es una vía.
        switch (viaAcceso)
        {
            case TipoViaAccesoAuditoria.Normal when viaAccesoId is null:
            case TipoViaAccesoAuditoria.OperacionDelegada when viaAccesoId is { } id && id != Guid.Empty:
                break;
            case TipoViaAccesoAuditoria.SesionPrivilegiada:
                throw new ArgumentException("Una Sesión Privilegiada no abre tareas del asistente.", nameof(viaAcceso));
            default:
                throw new ArgumentException(
                    "La vía de acceso debe ser la normal (sin identificador) o una operación delegada (con su Asignación de Operación).",
                    nameof(viaAcceso));
        }

        ActorRealUsuarioId = actorRealUsuarioId;
        UsuarioSimuladoId = usuarioSimuladoId;
        TenantOrigenId = tenantOrigenId;
        ViaAcceso = viaAcceso;
        ViaAccesoId = viaAccesoId;
        Estado = EstadoTareaAsistente.Conversando;
        CreadaEnUtc = ahoraUtc;
        ActualizadaEnUtc = ahoraUtc;
    }

    /// <summary>La persona escribe. <paramref name="textoEnmascarado"/> es lo que salió hacia el proveedor, si salió algo.</summary>
    public TurnoTareaAsistente AgregarTurnoDePersona(string textoOriginal, string? textoEnmascarado, DateTime ahoraUtc) =>
        AgregarTurno(AutorTurnoTareaAsistente.Persona, textoOriginal, textoEnmascarado, ahoraUtc);

    /// <summary>El asistente responde. <paramref name="textoEnmascarado"/> es lo que llegó del proveedor, si llegó algo.</summary>
    public TurnoTareaAsistente AgregarTurnoDeAsistente(string textoOriginal, string? textoEnmascarado, DateTime ahoraUtc) =>
        AgregarTurno(AutorTurnoTareaAsistente.Asistente, textoOriginal, textoEnmascarado, ahoraUtc);

    /// <summary>
    /// Guarda (o sustituye) el plan entero. Solo antes de confirmar: un plan
    /// confirmado no se reescribe.
    /// </summary>
    public void GuardarPlan(IReadOnlyList<DefinicionPasoTareaAsistente> pasos, bool asistidoPorIa, DateTime ahoraUtc)
    {
        ArgumentNullException.ThrowIfNull(pasos);
        ExigirPlanModificable();
        if (pasos.Count == 0)
            throw new ArgumentException("Un plan debe tener al menos un paso.", nameof(pasos));
        if (pasos.Count > MaximoPasos)
            throw new ArgumentException($"Un plan no puede tener más de {MaximoPasos} pasos.", nameof(pasos));

        var nuevos = pasos
            .Select((definicion, indice) => new PasoTareaAsistente(
                Id, indice + 1, definicion ?? throw new ArgumentNullException(nameof(pasos)), asistidoPorIa, ahoraUtc))
            .ToList();

        _pasos.Clear();
        _pasos.AddRange(nuevos);
        RecalcularEstadoDelPlan(ahoraUtc);
    }

    /// <summary>Llega el dato que faltaba (o cambia): el paso se reanuda desde su borrador.</summary>
    public void ActualizarPasoEnBorrador(
        Guid pasoId,
        string datosJson,
        string? resumen,
        IReadOnlyList<string> camposPendientes,
        IReadOnlyList<AvisoPasoTareaAsistente> avisos,
        DateTime ahoraUtc)
    {
        ExigirPlanModificable();
        BuscarPaso(pasoId).ActualizarBorrador(datosJson, resumen, camposPendientes, avisos, ahoraUtc);
        RecalcularEstadoDelPlan(ahoraUtc);
    }

    /// <summary>La persona retira un paso del plan antes de confirmarlo.</summary>
    public void DescartarPaso(Guid pasoId, DateTime ahoraUtc)
    {
        ExigirPlanModificable();
        BuscarPaso(pasoId).Descartar(ahoraUtc);
        RecalcularEstadoDelPlan(ahoraUtc);
    }

    /// <summary>
    /// La persona confirma el plan entero (el Enter). Exige que todos los pasos
    /// vivos estén listos y que quien confirma sea la persona propietaria.
    /// </summary>
    public void ConfirmarPlan(Guid actorRealUsuarioId, Guid? usuarioSimuladoId, DateTime ahoraUtc)
    {
        if (Estado != EstadoTareaAsistente.PlanListo)
            throw new InvalidOperationException($"Solo se confirma un plan listo; la tarea está {Estado}.");
        if (actorRealUsuarioId != ActorRealUsuarioId)
            throw new InvalidOperationException("Solo la persona propietaria de la tarea confirma su plan.");
        if (usuarioSimuladoId == Guid.Empty)
            throw new ArgumentException("El Usuario simulado, si existe, debe ser un usuario.", nameof(usuarioSimuladoId));

        foreach (var paso in _pasos.Where(p => p.EstaVivo))
            paso.Confirmar(ahoraUtc);

        PlanConfirmadoPorActorRealUsuarioId = actorRealUsuarioId;
        PlanConfirmadoComoUsuarioSimuladoId = usuarioSimuladoId;
        PlanConfirmadoEnUtc = ahoraUtc;
        Estado = EstadoTareaAsistente.Confirmada;
        Tocar(ahoraUtc);
    }

    /// <summary>
    /// El Command del paso terminó bien. Idempotente: repetirlo con el mismo
    /// resultado no cambia nada.
    /// </summary>
    public void RegistrarPasoEjecutado(Guid pasoId, Guid? entidadResultadoId, DateTime ahoraUtc)
    {
        ExigirPlanConfirmado();
        if (!BuscarPaso(pasoId).RegistrarEjecucion(entidadResultadoId, ahoraUtc))
            return;

        if (_pasos.Where(p => p.EstaVivo).All(p => p.Estado == EstadoPasoTareaAsistente.Ejecutado))
            Estado = EstadoTareaAsistente.Terminada;
        Tocar(ahoraUtc);
    }

    /// <summary>El Command del paso falló; el paso queda para reintentar.</summary>
    public void RegistrarPasoFallido(Guid pasoId, string motivo, DateTime ahoraUtc)
    {
        ExigirPlanConfirmado();
        BuscarPaso(pasoId).RegistrarFallo(motivo, ahoraUtc);
        Tocar(ahoraUtc);
    }

    /// <summary>La persona abandona la tarea. Lo ya ejecutado no se deshace.</summary>
    public void Descartar(DateTime ahoraUtc)
    {
        if (EstaCerrada)
            throw new InvalidOperationException($"La tarea ya está {Estado}.");

        foreach (var paso in _pasos)
            paso.Descartar(ahoraUtc);

        Estado = EstadoTareaAsistente.Descartada;
        DescartadaEnUtc = ahoraUtc;
        Tocar(ahoraUtc);
    }

    private TurnoTareaAsistente AgregarTurno(
        AutorTurnoTareaAsistente autor, string textoOriginal, string? textoEnmascarado, DateTime ahoraUtc)
    {
        if (EstaCerrada)
            throw new InvalidOperationException($"No se conversa en una tarea {Estado}.");
        if (_turnos.Count >= MaximoTurnos)
            throw new InvalidOperationException($"Una tarea no admite más de {MaximoTurnos} turnos.");

        var turno = new TurnoTareaAsistente(Id, _turnos.Count + 1, autor, textoOriginal, textoEnmascarado, ahoraUtc);
        _turnos.Add(turno);
        Tocar(ahoraUtc);
        return turno;
    }

    private void ExigirPlanModificable()
    {
        if (Estado is not (EstadoTareaAsistente.Conversando or EstadoTareaAsistente.PlanEnBorrador or EstadoTareaAsistente.PlanListo))
            throw new InvalidOperationException($"El plan ya no se puede modificar: la tarea está {Estado}.");
    }

    private void ExigirPlanConfirmado()
    {
        if (Estado != EstadoTareaAsistente.Confirmada || PlanConfirmadoEnUtc is null)
            throw new InvalidOperationException($"Nada se ejecuta sin un plan confirmado; la tarea está {Estado}.");
    }

    private PasoTareaAsistente BuscarPaso(Guid pasoId) =>
        _pasos.FirstOrDefault(p => p.Id == pasoId)
        ?? throw new ArgumentException("El paso no pertenece a esta tarea.", nameof(pasoId));

    private void RecalcularEstadoDelPlan(DateTime ahoraUtc)
    {
        var vivos = _pasos.Where(p => p.EstaVivo).ToList();
        Estado = vivos.Count > 0 && vivos.All(p => p.Estado == EstadoPasoTareaAsistente.Listo)
            ? EstadoTareaAsistente.PlanListo
            : EstadoTareaAsistente.PlanEnBorrador;
        Tocar(ahoraUtc);
    }

    private void Tocar(DateTime ahoraUtc) => ActualizadaEnUtc = ahoraUtc;
}
