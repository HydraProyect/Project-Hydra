namespace CaeManager.Domain.Plantillas;

/// <summary>
/// ADR-010 § 2.3: mientras el gestor edita, la versión no es una plantilla
/// operacional — solo <see cref="Confirmada"/> se usa para generar
/// documentos. Una versión confirmada nunca está parcialmente configurada.
/// </summary>
public enum EstadoConfiguracionPlantilla
{
    /// <summary>Creada, sin propuesta de detección todavía o en edición activa por el gestor.</summary>
    Borrador,

    /// <summary>Detección ejecutada y elementos propuestos — pendiente de que el gestor los revise y confirme.</summary>
    PendienteRevision,

    /// <summary>Fuente de verdad determinista para generar documentos — inmutable a partir de aquí (crear una versión nueva en vez de editarla).</summary>
    Confirmada
}
