using CaeManager.Domain.Comunicaciones;

namespace CaeManager.Application.Comunicaciones;

public interface IComunicacionesQueryContext
{
    IQueryable<Conversacion> Conversaciones { get; }
    IQueryable<Mensaje> Mensajes { get; }
    IQueryable<ParticipanteConversacion> ParticipantesConversacion { get; }
    IQueryable<MacroRespuesta> MacrosRespuesta { get; }
    IQueryable<AdjuntoMensaje> AdjuntosMensaje { get; }
    IQueryable<SugerenciaVisitaCorreo> SugerenciasVisitaCorreo { get; }
    IQueryable<SugerenciaGestionCorreo> SugerenciasGestionCorreo { get; }
    IQueryable<DetalleSugerenciaGestionCorreo> DetallesSugerenciaGestionCorreo { get; }
    IQueryable<ClasificacionRuidoDetalleGestion> ClasificacionesRuidoDetalleGestion { get; }
    IQueryable<ContactoWhatsApp> ContactosWhatsApp { get; }
    IQueryable<SolicitudPrioridadDocumento> SolicitudesPrioridadDocumento { get; }
    IQueryable<EventoConversacion> EventosConversacion { get; }
    IQueryable<ClasificacionRuidoMensaje> ClasificacionesRuidoMensaje { get; }
}
