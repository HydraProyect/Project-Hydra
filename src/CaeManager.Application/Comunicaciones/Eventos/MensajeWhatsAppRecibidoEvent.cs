using MediatR;

namespace CaeManager.Application.Comunicaciones.Eventos;

/// <summary>
/// Primer <see cref="INotification"/> del repositorio (ARQUITECTURA-INTEGRACIONES.md
/// § 6.5: "el orquestador publica, no invoca"): la ingesta de WhatsApp lo
/// publica DESPUÉS de confirmar la transacción — nunca antes, un aviso de un
/// mensaje no persistido rompería a los suscriptores — y no conoce a sus
/// consumidores (hoy el notificador de tiempo real de la UI; mañana IA o
/// alertas sin tocar la ingesta).
/// </summary>
public sealed record MensajeWhatsAppRecibidoEvent(Guid TenantId, Guid ConversacionId) : INotification;
