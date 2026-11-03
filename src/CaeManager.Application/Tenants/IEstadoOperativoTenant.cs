namespace CaeManager.Application.Tenants;

/// <summary>
/// Lo que preguntan las puertas de sesión (inicio, cookie, circuito, extensión) en el instante de
/// entrar: si el Tenant está en <see cref="CaeManager.Domain.Tenants.EstadoTenant.Suspendido"/>.
/// Una fila inexistente no cuenta como suspendida, y un fallo de lectura se propaga: la puerta falla
/// cerrada.
/// </summary>
public interface IEstadoOperativoTenant
{
    Task<bool> EstaSuspendidoAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
