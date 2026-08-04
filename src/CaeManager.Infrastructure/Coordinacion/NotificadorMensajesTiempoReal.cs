using System.Collections.Concurrent;
using CaeManager.Application.Comunicaciones.Eventos;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Coordinacion;

/// <summary>
/// Implementación en proceso de <see cref="INotificadorMensajesTiempoReal"/>
/// — singleton. Los callbacks se invocan uno a uno con try/catch individual:
/// un circuito muerto (pestaña cerrada sin dispose todavía) no puede impedir
/// que el resto reciba su aviso.
/// </summary>
public class NotificadorMensajesTiempoReal(ILogger<NotificadorMensajesTiempoReal> logger) : INotificadorMensajesTiempoReal
{
    private sealed class Suscripcion(
        NotificadorMensajesTiempoReal notificador, Guid tenantId, Func<MensajeWhatsAppRecibidoEvent, Task> callback)
        : IDisposable
    {
        public Guid TenantId { get; } = tenantId;
        public Func<MensajeWhatsAppRecibidoEvent, Task> Callback { get; } = callback;

        public void Dispose() => notificador.Desuscribir(this);
    }

    private readonly ConcurrentDictionary<Guid, List<Suscripcion>> _suscripcionesPorTenant = new();

    public IDisposable Suscribir(Guid tenantId, Func<MensajeWhatsAppRecibidoEvent, Task> callback)
    {
        var suscripcion = new Suscripcion(this, tenantId, callback);
        var lista = _suscripcionesPorTenant.GetOrAdd(tenantId, _ => []);
        lock (lista) lista.Add(suscripcion);
        return suscripcion;
    }

    public async Task PublicarAsync(MensajeWhatsAppRecibidoEvent aviso, CancellationToken cancellationToken = default)
    {
        if (!_suscripcionesPorTenant.TryGetValue(aviso.TenantId, out var lista))
            return;

        Suscripcion[] instantanea;
        lock (lista) instantanea = [.. lista];

        foreach (var suscripcion in instantanea)
        {
            try
            {
                await suscripcion.Callback(aviso);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Un suscriptor de tiempo real falló al recibir el aviso (circuito probablemente muerto).");
            }
        }
    }

    private void Desuscribir(Suscripcion suscripcion)
    {
        if (_suscripcionesPorTenant.TryGetValue(suscripcion.TenantId, out var lista))
            lock (lista) lista.Remove(suscripcion);
    }
}
