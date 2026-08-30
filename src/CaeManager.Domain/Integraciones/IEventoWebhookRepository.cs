namespace CaeManager.Domain.Integraciones;

public interface IEventoWebhookRepository
{
    /// <summary>
    /// Reclama atómicamente el evento <see cref="EstadoEventoWebhook.Pendiente"/>
    /// más antiguo cuya conexión pertenece al proveedor indicado y cuyo
    /// <see cref="EventoWebhook.SiguienteIntentoEnUtc"/> ya pasó (o nunca se
    /// fijó) — cada consumidor (Microsoft 365, WhatsApp) drena SOLO su cola,
    /// para que un backlog de correo no retrase el chat ni al revés.
    ///
    /// Lo marca <see cref="EstadoEventoWebhook.Procesando"/> y confirma ambas
    /// cosas dentro de una única transacción con <c>FOR UPDATE SKIP LOCKED</c>
    /// — sin esto, el SELECT y el UPDATE posterior dejaban una ventana sin
    /// bloqueo de fila cuya única exclusión real era el advisory lock de
    /// elección de líder (frágil si esa conexión se cae a mitad de un lote).
    /// </summary>
    Task<EventoWebhook?> ReclamarSiguientePendienteAsync(ProveedorIntegracion proveedor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Eventos del proveedor indicado que llevan más de <paramref name="umbral"/>
    /// en <see cref="EstadoEventoWebhook.Procesando"/> — candidatos a
    /// <see cref="EventoWebhook.RecuperarSiEstancado"/>.
    /// </summary>
    Task<IReadOnlyList<EventoWebhook>> ObtenerEstancadosAsync(
        ProveedorIntegracion proveedor, TimeSpan umbral, CancellationToken cancellationToken = default);

    void Agregar(EventoWebhook evento);
}
