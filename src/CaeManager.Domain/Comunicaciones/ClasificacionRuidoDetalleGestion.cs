using CaeManager.Domain.Common;

namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// Marca un ítem de gestión (DetalleSugerenciaGestionCorreo) como repetición de un pendiente que
/// ya se reclamó formalmente al Cliente — ronda de reducción de ruido en Comunicaciones.
///
/// La evidencia es siempre un registro formal: una <see cref="Reclamaciones.ReclamacionDocumentalDocumento"/>
/// cuyo Documento resuelve al mismo (Trabajador, TipoDocumento) que el ítem — nunca una simple
/// sospecha de la IA. En este negocio toda solicitud de documentación se hace por correo y siempre
/// debe quedar registro formal, así que exigir esta evidencia no pierde cobertura del caso real:
/// si de verdad se pidió, esta reclamación existe.
///
/// Solo se calcula para ítems de mensajes ya marcados como notificación automática
/// (<see cref="ClasificacionRuidoMensaje.EsNotificacionAutomatica"/>) — nunca sobre un correo humano.
/// </summary>
public class ClasificacionRuidoDetalleGestion : EntidadConTenant
{
    public Guid DetalleSugerenciaGestionCorreoId { get; private set; }
    public Guid ReclamacionDocumentalDocumentoId { get; private set; }

    /// <summary>El gestor confirmó que este ítem sí importa — deja de tratarse como ruido.</summary>
    public bool ConfirmadaManualmente { get; private set; }

    public DateTime CreadaEnUtc { get; private set; } = DateTime.UtcNow;

    private ClasificacionRuidoDetalleGestion()
    {
    }

    public ClasificacionRuidoDetalleGestion(Guid detalleSugerenciaGestionCorreoId, Guid reclamacionDocumentalDocumentoId)
    {
        if (detalleSugerenciaGestionCorreoId == Guid.Empty)
            throw new ArgumentException("La clasificación debe estar ligada a un ítem de sugerencia.", nameof(detalleSugerenciaGestionCorreoId));
        if (reclamacionDocumentalDocumentoId == Guid.Empty)
            throw new ArgumentException("La clasificación debe apoyarse en una reclamación formal.", nameof(reclamacionDocumentalDocumentoId));

        DetalleSugerenciaGestionCorreoId = detalleSugerenciaGestionCorreoId;
        ReclamacionDocumentalDocumentoId = reclamacionDocumentalDocumentoId;
    }

    public void ConfirmarManualmente() => ConfirmadaManualmente = true;
}
