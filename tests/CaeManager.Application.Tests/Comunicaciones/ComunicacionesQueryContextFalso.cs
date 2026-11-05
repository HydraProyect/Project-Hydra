using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Comunicaciones;

namespace CaeManager.Application.Tests.Comunicaciones;

public class ComunicacionesQueryContextFalso : IComunicacionesQueryContext
{
    public List<Conversacion> ListaConversaciones { get; } = [];
    public IQueryable<Conversacion> Conversaciones
    {
        get
        {
            ContarConsultaFueraDeInstantanea();
            return new TestAsyncQueryable<Conversacion>(ListaConversaciones.AsQueryable());
        }
    }
    public List<Mensaje> ListaMensajes { get; } = [];
    public IQueryable<Mensaje> Mensajes
    {
        get
        {
            ContarConsultaFueraDeInstantanea();
            return new TestAsyncQueryable<Mensaje>(ListaMensajes.AsQueryable());
        }
    }
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
    public List<RespuestaSugerida> ListaRespuestasSugeridas { get; } = [];

    /// <summary>Instantáneas de lectura abiertas (<see cref="EnInstantaneaDeLecturaAsync{T}"/>).</summary>
    public int InstantaneasAbiertas { get; private set; }

    public bool DentroDeInstantanea { get; private set; }

    /// <summary>Consultas a Conversaciones, Mensajes o RespuestasSugeridas hechas sin instantánea abierta.</summary>
    public int ConsultasFueraDeInstantanea { get; private set; }

    private List<RespuestaSugerida>? _respuestasAlAbrirLaInstantanea;

    private void ContarConsultaFueraDeInstantanea()
    {
        if (!DentroDeInstantanea)
            ConsultasFueraDeInstantanea++;
    }

    /// <summary>
    /// Modela la instantánea en lo que un doble en memoria puede: mientras está abierta, las sugerencias que se añaden a la
    /// lista no se ven. No modela los cambios de estado de las entidades ya existentes (comparten referencia). Dentro de
    /// otra instantánea (ambiente) ejecuta la lectura sin abrir una nueva, como la implementación real.
    /// </summary>
    public async Task<T> EnInstantaneaDeLecturaAsync<T>(Func<CancellationToken, Task<T>> lectura, CancellationToken cancellationToken = default)
    {
        if (DentroDeInstantanea)
            return await lectura(cancellationToken);

        InstantaneasAbiertas++;
        _respuestasAlAbrirLaInstantanea = [.. ListaRespuestasSugeridas];
        DentroDeInstantanea = true;
        try
        {
            return await lectura(cancellationToken);
        }
        finally
        {
            DentroDeInstantanea = false;
            _respuestasAlAbrirLaInstantanea = null;
        }
    }
    public int LecturasDeRespuestasSugeridas { get; private set; }

    /// <summary>Se invoca al empezar cada lectura de sugerencias, con su número de orden (1 la primera): permite cambiar los datos entre dos lecturas.</summary>
    public Action<int>? AlLeerRespuestasSugeridas { get; set; }

    public IQueryable<RespuestaSugerida> RespuestasSugeridas
    {
        get
        {
            LecturasDeRespuestasSugeridas++;
            ContarConsultaFueraDeInstantanea();
            AlLeerRespuestasSugeridas?.Invoke(LecturasDeRespuestasSugeridas);
            // Dentro de la instantánea se lee la lista tal como estaba al abrirla: lo añadido después, no se ve.
            return new TestAsyncQueryable<RespuestaSugerida>((_respuestasAlAbrirLaInstantanea ?? ListaRespuestasSugeridas).AsQueryable());
        }
    }
    public List<ClasificacionRuidoMensaje> ListaClasificacionesRuidoMensaje { get; } = [];
    public IQueryable<ClasificacionRuidoMensaje> ClasificacionesRuidoMensaje => new TestAsyncQueryable<ClasificacionRuidoMensaje>(ListaClasificacionesRuidoMensaje.AsQueryable());
    public List<ClasificacionRelevanciaCae> ListaClasificacionesRelevanciaCae { get; } = [];
    public IQueryable<ClasificacionRelevanciaCae> ClasificacionesRelevanciaCae => new TestAsyncQueryable<ClasificacionRelevanciaCae>(ListaClasificacionesRelevanciaCae.AsQueryable());
    public List<IntencionConversacion> ListaIntencionesConversacion { get; } = [];
    public IQueryable<IntencionConversacion> IntencionesConversacion => new TestAsyncQueryable<IntencionConversacion>(ListaIntencionesConversacion.AsQueryable());
}
