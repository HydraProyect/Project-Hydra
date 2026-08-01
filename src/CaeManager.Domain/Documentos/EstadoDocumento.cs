namespace CaeManager.Domain.Documentos;

/// <summary>
/// Estado de vigencia de un Documento. Nunca se persiste: se calcula siempre
/// a partir de FechaVencimiento y los umbrales configurables de
/// ParametroSistema (ver CalculadoraEstadoDocumento y DATABASE.md).
/// </summary>
public enum EstadoDocumento
{
    /// <summary>El tipo de documento no genera vencimiento (p. ej. Formación 60h).</summary>
    NoAplica,
    Vigente,
    Proximo,
    Urgente,
    Vencido,

    /// <summary>
    /// No es un estado de vigencia — no hay ningún Documento que evaluar.
    /// Un Trabajador con Asignación activa a un Centro que exige un
    /// TipoDocumento obligatorio (ver ObtenerAlertasQuery) y ningún
    /// Documento de ese tipo. <see cref="CalculadoraEstadoDocumento"/> nunca
    /// produce este valor — solo lo calcula la Query de Alertas, que sí
    /// sabe qué debería existir y no solo qué existe.
    /// </summary>
    Faltante
}
