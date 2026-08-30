namespace CaeManager.Domain.Comunicaciones;

public interface IConversacionRepository
{
    /// <summary>Incluye Mensajes y Participantes — es lo que necesita la pantalla de detalle/respuesta.</summary>
    Task<Conversacion?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Para la ingesta de webhook (P3-33): encuentra el hilo existente al que
    /// se suma un mensaje entrante nuevo, o null si es el primero del hilo.
    /// Acotado por <paramref name="conexionIntegracionId"/> (auditoría módulo
    /// 6) — Graph puede asignar el MISMO conversationId a un hilo que
    /// participan dos buzones conectados distintos del mismo tenant (está
    /// documentado por Microsoft: comparten conversationId los
    /// participantes de Exchange Online de la misma organización); sin este
    /// filtro, el mensaje de un buzón se colaría en el hilo del otro.
    /// </summary>
    Task<Conversacion?> ObtenerPorHiloExternoAsync(
        Guid conexionIntegracionId, string hiloExternoId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Idempotencia ante reintentos de notificación de webhook (P3-33): true
    /// si ya existe un Mensaje con ese Id externo (Message-Id de Graph o
    /// wamid de WhatsApp) EN ESA CONEXIÓN. Acotado por
    /// <paramref name="conexionIntegracionId"/> (auditoría módulo 6): el
    /// espacio de Message-Id/wamid pertenece al buzón/línea que lo emitió,
    /// no al tenant entero — sin este filtro, dos conexiones del mismo
    /// tenant no podrían tener nunca el mismo Id externo aunque pertenezcan
    /// a proveedores o buzones distintos. No hay índice único compuesto con
    /// ConexionIntegracionId en Mensajes (ver MensajeConfiguration — mismo
    /// motivo que impide una FK compuesta con TenantId, auditoría Módulo 8):
    /// el filtro se aplica aquí, en la consulta, vía el Id de conversación.
    /// </summary>
    Task<bool> ExisteMensajeExternoAsync(Guid conexionIntegracionId, string mensajeExternoId, CancellationToken cancellationToken = default);

    /// <summary>Hilo WhatsApp: la conversación más reciente NO cerrada de ese teléfono en esa línea, o null si toca crear una nueva. Incluye Mensajes.</summary>
    Task<Conversacion?> ObtenerAbiertaPorTelefonoAsync(Guid conexionIntegracionId, string telefonoContacto, CancellationToken cancellationToken = default);

    /// <summary>
    /// Para los statuses[] de WhatsApp (delivered/read/failed): localiza el
    /// mensaje saliente por su wamid, acotado a <paramref name="conexionIntegracionId"/>
    /// — mismo motivo de aislamiento por conexión que <see cref="ExisteMensajeExternoAsync"/>.
    /// </summary>
    Task<Mensaje?> ObtenerMensajePorExternoIdAsync(Guid conexionIntegracionId, string mensajeExternoId, CancellationToken cancellationToken = default);

    /// <summary>Reparto equitativo del pool inbound: conversaciones WhatsApp vivas (Abierta/Pendiente) asignadas a cada uno de los ejecutivos dados. Los ejecutivos sin ninguna no aparecen en el diccionario.</summary>
    Task<IReadOnlyDictionary<Guid, int>> ContarWhatsAppVivasPorEjecutivoAsync(
        IReadOnlyCollection<Guid> ejecutivoIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Candidatas del Conversation Matching Engine (§ 13.2): conversaciones
    /// Abierta/Pendiente del mismo Cliente, excluyendo <paramref name="excluirConversacionId"/>.
    /// Sin Mensajes/Participantes incluidos — el motor de coincidencia de hoy
    /// no los necesita (ver MotorCoincidenciaConversacionesService).
    /// </summary>
    Task<IReadOnlyList<Conversacion>> ObtenerAbiertasPorClienteAsync(
        Guid clienteId, Guid excluirConversacionId, CancellationToken cancellationToken = default);

    void Agregar(Conversacion conversacion);
}
