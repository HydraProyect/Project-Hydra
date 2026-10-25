using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.EstadoMaterializado.Frescura;

/// <summary>
/// Implementación en proceso de <see cref="INotificadorEstadoTenant"/>, con el patrón de
/// <c>NotificadorMensajesTiempoReal</c>: suscripciones por <c>TenantId</c> y publicación solo a las de ese Tenant. Cada
/// callback va con su propio try/catch: un circuito muerto (pestaña cerrada sin liberar todavía) no impide que el resto
/// reciba su aviso.
/// </summary>
public sealed class NotificadorEstadoTenant(ILogger<NotificadorEstadoTenant>? logger = null) : INotificadorEstadoTenant
{
    private sealed class Suscripcion(NotificadorEstadoTenant notificador, Guid tenantId, Action<AvisoCambioEstado> alRecibir) : IDisposable
    {
        public Guid TenantId { get; } = tenantId;
        public Action<AvisoCambioEstado> AlRecibir { get; } = alRecibir;

        public void Dispose() => notificador.Desuscribir(this);
    }

    private readonly ConcurrentDictionary<Guid, List<Suscripcion>> _porTenant = new();

    public IDisposable Suscribir(Guid tenantId, Action<AvisoCambioEstado> alRecibir)
    {
        ArgumentNullException.ThrowIfNull(alRecibir);
        var suscripcion = new Suscripcion(this, tenantId, alRecibir);
        var lista = _porTenant.GetOrAdd(tenantId, _ => []);
        lock (lista) lista.Add(suscripcion);
        return suscripcion;
    }

    public void Publicar(AvisoCambioEstado aviso)
    {
        ArgumentNullException.ThrowIfNull(aviso);
        if (!_porTenant.TryGetValue(aviso.TenantId, out var lista))
            return;

        Suscripcion[] instantanea;
        lock (lista) instantanea = [.. lista];

        foreach (var suscripcion in instantanea)
        {
            try
            {
                suscripcion.AlRecibir(aviso);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Un suscriptor del estado materializado falló al recibir el aviso (circuito probablemente cerrado).");
            }
        }
    }

    public int SuscripcionesDe(Guid tenantId)
    {
        if (!_porTenant.TryGetValue(tenantId, out var lista))
            return 0;
        lock (lista) return lista.Count;
    }

    private void Desuscribir(Suscripcion suscripcion)
    {
        if (_porTenant.TryGetValue(suscripcion.TenantId, out var lista))
            lock (lista) lista.Remove(suscripcion);
    }
}
