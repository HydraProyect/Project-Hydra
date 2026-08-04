namespace CaeManager.Domain.Comunicaciones;

public interface IConversacionCorreoRepository
{
    /// <summary>Incluye Mensajes y Participantes — es lo que necesita la pantalla de detalle/respuesta.</summary>
    Task<ConversacionCorreo?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Para la ingesta de webhook (P3-33): encuentra el hilo existente al que se suma un mensaje entrante nuevo, o null si es el primero del hilo.</summary>
    Task<ConversacionCorreo?> ObtenerPorHiloExternoAsync(string hiloExternoId, CancellationToken cancellationToken = default);

    /// <summary>Idempotencia ante reintentos de notificación de webhook (P3-33): true si ya existe un MensajeCorreo con ese Id externo (Message-Id de Graph o wamid de WhatsApp).</summary>
    Task<bool> ExisteMensajeExternoAsync(string mensajeExternoId, CancellationToken cancellationToken = default);

    /// <summary>Hilo WhatsApp: la conversación más reciente NO cerrada de ese teléfono en esa línea, o null si toca crear una nueva. Incluye Mensajes.</summary>
    Task<ConversacionCorreo?> ObtenerAbiertaPorTelefonoAsync(Guid conexionIntegracionId, string telefonoContacto, CancellationToken cancellationToken = default);

    /// <summary>Para los statuses[] de WhatsApp (delivered/read/failed): localiza el mensaje saliente por su wamid.</summary>
    Task<MensajeCorreo?> ObtenerMensajePorExternoIdAsync(string mensajeExternoId, CancellationToken cancellationToken = default);

    void Agregar(ConversacionCorreo conversacion);
}
