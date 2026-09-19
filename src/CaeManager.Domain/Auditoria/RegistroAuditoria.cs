using CaeManager.Domain.Common;

namespace CaeManager.Domain.Auditoria;

/// <summary>
/// Registro de un cambio sobre una entidad de dominio. Lo escribe el
/// interceptor de EF Core en Infrastructure en cada SaveChanges — nunca se
/// crea manualmente desde Application (ver ARCHITECTURE.md, "Auditoría y
/// soft delete").
/// </summary>
public class RegistroAuditoria : EntidadConTenant
{
    public string EntidadTipo { get; private set; } = string.Empty;
    public Guid EntidadId { get; private set; }
    public string Accion { get; private set; } = string.Empty;
    public string? DatosAntes { get; private set; }
    public string? DatosDespues { get; private set; }

    /// <summary>
    /// Quien figura como autor del cambio. Durante una impersonación es el
    /// usuario <b>simulado</b>: es lo que hay que mirar para responder "¿qué
    /// se hizo en nombre de Juan?".
    /// </summary>
    public Guid? UsuarioId { get; private set; }

    /// <summary>
    /// Quien estaba realmente detrás del teclado. Coincide con
    /// <see cref="UsuarioId"/> salvo durante una impersonación, y es lo que
    /// hace que una acción hecha simulando a alguien sea distinguible a
    /// posteriori de una que hizo esa persona (ADR-011 § 8.4).
    ///
    /// Nulo en las filas anteriores a esta columna y en los guardados sin
    /// identidad resuelta (jobs de fondo, seeders). Ese nulo no dice si detrás
    /// había una persona sin identificar o una máquina: eso lo responde
    /// <see cref="TipoActor"/>.
    /// </summary>
    public Guid? ActorRealUsuarioId { get; private set; }

    /// <summary>
    /// Desde dónde se operaba. Nulo solo en las filas históricas, que se leen
    /// como <c>Normal</c>.
    /// </summary>
    public TipoViaAccesoAuditoria? ViaAcceso { get; private set; }

    /// <summary>La fila que ampara la vía: la operación delegada o la sesión privilegiada.</summary>
    public Guid? ViaAccesoId { get; private set; }

    /// <summary>
    /// Qué clase de actor provocó el cambio: una persona, la propia plataforma o
    /// un tercero con clave de API. Eje <b>ortogonal</b> a
    /// <see cref="ViaAcceso"/> — ver <see cref="TipoActorAuditoria"/>.
    ///
    /// No anulable, con <c>Desconocido</c> en las filas anteriores a esta
    /// columna: para ellas no se infiere nada retroactivamente. Lo fija el
    /// interceptor a partir de <c>ActorAuditoria.ResolverTipoActor()</c>, que es
    /// la única regla que lo decide.
    /// </summary>
    public TipoActorAuditoria TipoActor { get; private set; }

    public DateTime FechaUtc { get; private set; }

    private RegistroAuditoria()
    {
    }

    public RegistroAuditoria(
        string entidadTipo,
        Guid entidadId,
        string accion,
        string? datosAntes,
        string? datosDespues,
        Guid? usuarioId,
        // OBLIGATORIO, y por eso va antes de los opcionales (C# no admite un
        // parámetro requerido detrás de uno con valor por defecto). Con un
        // defecto, un llamador nuevo que olvidara pasarlo escribiría
        // `Desconocido` sin que nada lo señalara — justo el hueco que este eje
        // existe para cerrar. Una fila sintética de test que de verdad no sabe
        // quién actuó lo dice de forma explícita con `TipoActorAuditoria.Desconocido`.
        TipoActorAuditoria tipoActor,
        Guid? actorRealUsuarioId = null,
        TipoViaAccesoAuditoria? viaAcceso = null,
        Guid? viaAccesoId = null)
    {
        EntidadTipo = entidadTipo;
        EntidadId = entidadId;
        Accion = accion;
        DatosAntes = datosAntes;
        DatosDespues = datosDespues;
        UsuarioId = usuarioId;
        ActorRealUsuarioId = actorRealUsuarioId;
        ViaAcceso = viaAcceso;
        ViaAccesoId = viaAccesoId;
        TipoActor = tipoActor;
        FechaUtc = DateTime.UtcNow;
    }
}
