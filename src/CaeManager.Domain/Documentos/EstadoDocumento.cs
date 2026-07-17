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
    Vencido
}
