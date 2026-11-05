using CaeManager.Domain.Comunicaciones;

namespace CaeManager.Application.Comunicaciones;

public interface IComunicacionesQueryContext
{
    IQueryable<IntencionConversacion> IntencionesConversacion { get; }
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
    IQueryable<NotaInternaConversacion> NotasInternasConversacion { get; }
    IQueryable<PropuestaRecordatorioConversacion> PropuestasRecordatorioConversacion { get; }
    IQueryable<RespuestaSugerida> RespuestasSugeridas { get; }
    IQueryable<ClasificacionRuidoMensaje> ClasificacionesRuidoMensaje { get; }
    IQueryable<ClasificacionRelevanciaCae> ClasificacionesRelevanciaCae { get; }

    /// <summary>
    /// Ejecuta <paramref name="lectura"/> sobre una instantánea consistente de la base: una transacción de solo lectura
    /// REPEATABLE READ que se abre antes de la primera consulta y se confirma al terminar, de modo que todas las consultas de
    /// <paramref name="lectura"/> ven el mismo estado confirmado y nada confirmado después de la primera es visible. Las
    /// consultas de <paramref name="lectura"/> deben hacerse a través de este mismo contexto. Puede ejecutarse más de una
    /// vez si la estrategia de reintentos lo repite, así que no debe tener efectos fuera de la lectura.
    ///
    /// <para>
    /// Si ya hay una transacción abierta en el contexto (ambiente), se ejecuta dentro de ella sin abrir otra: la
    /// instantánea es entonces la de esa transacción, y solo es estricta si ya era REPEATABLE READ o superior.
    /// </para>
    /// </summary>
    Task<T> EnInstantaneaDeLecturaAsync<T>(Func<CancellationToken, Task<T>> lectura, CancellationToken cancellationToken = default);
}
