namespace CaeManager.Application.Documentos;

/// <summary>
/// Una orden que cambia el estado de una Acreditación de plataforma. Desde D-4 (#2005, decisión del 2026-11-15) el sello
/// del expediente de una Visita usa la regla de bloqueo común, en la que un Pendiente en plataforma bloquea: cuando una
/// Acreditación deja de estar pendiente, el expediente puede quedar completo, igual que al subir un documento. Las
/// órdenes con este marcador reevalúan, tras su éxito, las Visitas del Documento de esa Acreditación (lo hace
/// <c>ReevaluarExpedientesTrasCambioDeAcreditacionBehavior</c>, en Visitas). El marcador vive en Documentos para que las
/// órdenes del Núcleo no dependan de Outbound. Lo congela <c>OrdenesDeAcreditacionReevaluanElExpedienteTests</c>: toda
/// orden con <c>AcreditacionId</c> lo lleva.
/// </summary>
public interface ICambiaEstadoDeAcreditacion
{
    Guid AcreditacionId { get; }
}
