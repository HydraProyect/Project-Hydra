using CaeManager.Domain.Common;

namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// Motivo por el que un Mensaje se trata como ruido y se minimiza en la UI — nunca se borra
/// (ronda de reducción de ruido en Comunicaciones). Se amplía en fases sucesivas: RepeticionPendiente
/// y ResumenSinCambios llegan con el patrón de plataformas que reclaman lo mismo una y otra vez;
/// TrabajadorFueraVentana con el patrón de temporeros sin asignación activa; CorreoInterno y
/// PosiblePhishing con el buzón personal del gestor.
/// </summary>
public enum MotivoRuidoMensaje
{
    Ninguno = 0,
    ResumenSinCambios = 1
}

/// <summary>
/// Clasificación de ruido de un Mensaje entrante — satélite 1:1, mismo patrón que
/// <see cref="SugerenciaVisitaCorreo"/>/<see cref="SugerenciaGestionCorreo"/>. Se calcula una única
/// vez al ingerir el mensaje (igual que las sugerencias de IA) y queda estable aunque el catálogo
/// de plataformas cambie después. <see cref="EsNotificacionAutomatica"/> es el gate que comparten
/// todos los patrones de ruido de esta ronda: ninguno actúa sobre un correo humano.
/// </summary>
public class ClasificacionRuidoMensaje : EntidadConTenant
{
    public Guid MensajeId { get; private set; }
    public bool EsNotificacionAutomatica { get; private set; }
    public Guid? ProveedorPlataformaCaeId { get; private set; }
    public MotivoRuidoMensaje Motivo { get; private set; }

    /// <summary>El gestor confirmó que este mensaje sí importa — deja de tratarse como ruido aunque la clasificación automática siga siendo la misma.</summary>
    public bool ConfirmadaManualmente { get; private set; }

    public DateTime CreadaEnUtc { get; private set; } = DateTime.UtcNow;

    private ClasificacionRuidoMensaje()
    {
    }

    public ClasificacionRuidoMensaje(
        Guid mensajeId, bool esNotificacionAutomatica, Guid? proveedorPlataformaCaeId, MotivoRuidoMensaje motivo = MotivoRuidoMensaje.Ninguno)
    {
        if (mensajeId == Guid.Empty)
            throw new ArgumentException("La clasificación debe estar ligada a un mensaje.", nameof(mensajeId));

        MensajeId = mensajeId;
        EsNotificacionAutomatica = esNotificacionAutomatica;
        ProveedorPlataformaCaeId = proveedorPlataformaCaeId;
        Motivo = motivo;
    }

    public void CambiarMotivo(MotivoRuidoMensaje motivo) => Motivo = motivo;

    public void ConfirmarManualmente() => ConfirmadaManualmente = true;
}
