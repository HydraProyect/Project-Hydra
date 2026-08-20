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
    /// identidad resuelta (jobs de fondo, seeders).
    /// </summary>
    public Guid? ActorRealUsuarioId { get; private set; }

    /// <summary>
    /// Desde dónde se operaba. Nulo solo en las filas históricas, que se leen
    /// como <c>Normal</c>.
    /// </summary>
    public TipoViaAccesoAuditoria? ViaAcceso { get; private set; }

    /// <summary>La fila que ampara la vía: la operación delegada o la sesión privilegiada.</summary>
    public Guid? ViaAccesoId { get; private set; }

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
        FechaUtc = DateTime.UtcNow;
    }
}
