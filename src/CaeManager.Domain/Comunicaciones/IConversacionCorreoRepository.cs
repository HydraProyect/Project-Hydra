namespace CaeManager.Domain.Comunicaciones;

public interface IConversacionCorreoRepository
{
    /// <summary>Incluye Mensajes y Participantes — es lo que necesita la pantalla de detalle/respuesta.</summary>
    Task<ConversacionCorreo?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Para la ingesta de webhook (P3-33): encuentra el hilo existente al que se suma un mensaje entrante nuevo, o null si es el primero del hilo.</summary>
    Task<ConversacionCorreo?> ObtenerPorHiloExternoAsync(string hiloExternoId, CancellationToken cancellationToken = default);

    /// <summary>Idempotencia ante reintentos de notificación de webhook (P3-33): true si ya existe un MensajeCorreo con ese Id de Graph.</summary>
    Task<bool> ExisteMensajeExternoAsync(string mensajeExternoId, CancellationToken cancellationToken = default);

    void Agregar(ConversacionCorreo conversacion);
}
