using CaeManager.Domain.Comunicaciones;

namespace CaeManager.Application.Comunicaciones;

public interface IComunicacionesQueryContext
{
    IQueryable<ConversacionCorreo> ConversacionesCorreo { get; }
    IQueryable<MensajeCorreo> MensajesCorreo { get; }
    IQueryable<ParticipanteConversacion> ParticipantesConversacion { get; }
    IQueryable<MacroRespuesta> MacrosRespuesta { get; }
}
