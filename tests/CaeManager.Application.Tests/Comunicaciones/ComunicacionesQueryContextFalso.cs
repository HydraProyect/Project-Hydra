using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Comunicaciones;

namespace CaeManager.Application.Tests.Comunicaciones;

public class ComunicacionesQueryContextFalso : IComunicacionesQueryContext
{
    public List<Conversacion> ListaConversaciones { get; } = [];
    public IQueryable<Conversacion> Conversaciones => new TestAsyncQueryable<Conversacion>(ListaConversaciones.AsQueryable());
    public List<Mensaje> ListaMensajes { get; } = [];
    public IQueryable<Mensaje> Mensajes => new TestAsyncQueryable<Mensaje>(ListaMensajes.AsQueryable());
    public List<ParticipanteConversacion> ListaParticipantesConversacion { get; } = [];
    public IQueryable<ParticipanteConversacion> ParticipantesConversacion => new TestAsyncQueryable<ParticipanteConversacion>(ListaParticipantesConversacion.AsQueryable());
    public List<MacroRespuesta> ListaMacrosRespuesta { get; } = [];
    public IQueryable<MacroRespuesta> MacrosRespuesta => new TestAsyncQueryable<MacroRespuesta>(ListaMacrosRespuesta.AsQueryable());
    public List<AdjuntoMensaje> ListaAdjuntosMensaje { get; } = [];
    public IQueryable<AdjuntoMensaje> AdjuntosMensaje => new TestAsyncQueryable<AdjuntoMensaje>(ListaAdjuntosMensaje.AsQueryable());
    public List<SugerenciaVisitaCorreo> ListaSugerenciasVisitaCorreo { get; } = [];
    public IQueryable<SugerenciaVisitaCorreo> SugerenciasVisitaCorreo => new TestAsyncQueryable<SugerenciaVisitaCorreo>(ListaSugerenciasVisitaCorreo.AsQueryable());
    public List<SugerenciaGestionCorreo> ListaSugerenciasGestionCorreo { get; } = [];
    public IQueryable<SugerenciaGestionCorreo> SugerenciasGestionCorreo => new TestAsyncQueryable<SugerenciaGestionCorreo>(ListaSugerenciasGestionCorreo.AsQueryable());
    public List<DetalleSugerenciaGestionCorreo> ListaDetallesSugerenciaGestionCorreo { get; } = [];
    public IQueryable<DetalleSugerenciaGestionCorreo> DetallesSugerenciaGestionCorreo => new TestAsyncQueryable<DetalleSugerenciaGestionCorreo>(ListaDetallesSugerenciaGestionCorreo.AsQueryable());
    public List<ClasificacionRuidoDetalleGestion> ListaClasificacionesRuidoDetalleGestion { get; } = [];
    public IQueryable<ClasificacionRuidoDetalleGestion> ClasificacionesRuidoDetalleGestion => new TestAsyncQueryable<ClasificacionRuidoDetalleGestion>(ListaClasificacionesRuidoDetalleGestion.AsQueryable());
    public List<ContactoWhatsApp> ListaContactosWhatsApp { get; } = [];
    public IQueryable<ContactoWhatsApp> ContactosWhatsApp => new TestAsyncQueryable<ContactoWhatsApp>(ListaContactosWhatsApp.AsQueryable());
    public List<SolicitudPrioridadDocumento> ListaSolicitudesPrioridadDocumento { get; } = [];
    public IQueryable<SolicitudPrioridadDocumento> SolicitudesPrioridadDocumento => new TestAsyncQueryable<SolicitudPrioridadDocumento>(ListaSolicitudesPrioridadDocumento.AsQueryable());
    public List<EventoConversacion> ListaEventosConversacion { get; } = [];
    public IQueryable<EventoConversacion> EventosConversacion => new TestAsyncQueryable<EventoConversacion>(ListaEventosConversacion.AsQueryable());
    public List<NotaInternaConversacion> ListaNotasInternasConversacion { get; } = [];
    public IQueryable<NotaInternaConversacion> NotasInternasConversacion => new TestAsyncQueryable<NotaInternaConversacion>(ListaNotasInternasConversacion.AsQueryable());
    public List<PropuestaRecordatorioConversacion> ListaPropuestasRecordatorioConversacion { get; } = [];
    public IQueryable<PropuestaRecordatorioConversacion> PropuestasRecordatorioConversacion => new TestAsyncQueryable<PropuestaRecordatorioConversacion>(ListaPropuestasRecordatorioConversacion.AsQueryable());
    public List<ClasificacionRuidoMensaje> ListaClasificacionesRuidoMensaje { get; } = [];
    public IQueryable<ClasificacionRuidoMensaje> ClasificacionesRuidoMensaje => new TestAsyncQueryable<ClasificacionRuidoMensaje>(ListaClasificacionesRuidoMensaje.AsQueryable());
    public List<ClasificacionRelevanciaCae> ListaClasificacionesRelevanciaCae { get; } = [];
    public IQueryable<ClasificacionRelevanciaCae> ClasificacionesRelevanciaCae => new TestAsyncQueryable<ClasificacionRelevanciaCae>(ListaClasificacionesRelevanciaCae.AsQueryable());
}
