using CaeManager.Domain.Common;

namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// Entrada de "Sistema/Evento" en el Unified Timeline de una Conversacion
/// (docs/COMUNICACIONES.md § 12.3) — un hecho ocurrido en otro módulo de
/// operación (p. ej. "se creó la visita V-2026-0815") que se mezcla
/// cronológicamente con los mensajes del hilo. No guarda un texto
/// descriptivo denormalizado a propósito: <see cref="ReferenciaId"/> apunta
/// a la entidad real (la Visita, el Documento…) y quien lea el timeline
/// resuelve el texto en el momento — evita que la descripción quede
/// desfasada si esa entidad cambia después de crear el evento.
/// </summary>
public class EventoConversacion : EntidadConTenant
{
    public Guid ConversacionId { get; private set; }
    public TipoEventoConversacion Tipo { get; private set; }

    /// <summary>Id de la entidad que originó el evento (la Visita creada, etc.). Sin FK real a propósito — vive en otro agregado, mismo criterio que ParticipanteConversacion.EntidadRelacionadaId.</summary>
    public Guid ReferenciaId { get; private set; }

    public DateTime FechaUtc { get; private set; }

    private EventoConversacion()
    {
    }

    public EventoConversacion(Guid conversacionId, TipoEventoConversacion tipo, Guid referenciaId, DateTime fechaUtc)
    {
        if (conversacionId == Guid.Empty)
            throw new ArgumentException("El evento debe pertenecer a una conversación.", nameof(conversacionId));
        if (referenciaId == Guid.Empty)
            throw new ArgumentException("El evento debe referenciar la entidad que lo originó.", nameof(referenciaId));

        ConversacionId = conversacionId;
        Tipo = tipo;
        ReferenciaId = referenciaId;
        FechaUtc = fechaUtc;
    }
}
