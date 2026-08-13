namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// Tipo de hecho del sistema registrado en el timeline de una Conversacion
/// (docs/COMUNICACIONES.md § 12.3/§ 16.7 — "Eventos del sistema en el
/// timeline: Visitas + Documentos en v1").
/// </summary>
public enum TipoEventoConversacion
{
    VisitaCreada = 0,
    DocumentoActualizado = 1,

    /// <summary>
    /// Registrado en la conversación DESTINO al confirmar una propuesta del
    /// Conversation Matching Engine (§ 13.2) — <c>ReferenciaId</c> guarda el
    /// Id de la conversación ORIGEN, ya cerrada y sin mensajes propios.
    /// </summary>
    ConversacionVinculada = 2,

    /// <summary>
    /// Un lote de reclamación documental salió por esta conversación —
    /// <c>ReferenciaId</c> guarda el Id de la <c>ReclamacionDocumental</c>, que
    /// es quien sabe qué documentos se reclamaron. A diferencia de los otros
    /// tres, este evento nace en la MISMA conversación que el mensaje saliente
    /// que lo provocó: sirve para distinguir en el timeline un correo redactado
    /// a mano de uno que salió de la operación documental.
    /// </summary>
    ReclamacionEnviada = 3
}
