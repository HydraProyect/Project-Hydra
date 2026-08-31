using CaeManager.Application.Integraciones;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Integraciones;

/// <summary>
/// <c>IgnoreQueryFilters()</c> justificado (revisión explícita, ver
/// CLAUDE.md): en este punto de la petición no hay ningún tenant resuelto
/// todavía —es exactamente lo que este método existe para resolver—, así
/// que el filtro global de tenant no puede estar activo. Mismo patrón ya
/// usado en <c>ApiKeyAuthenticationHandler.ObtenerPorHashAsync</c>: el
/// filtro se retoma en cuanto <see cref="VerificacionWebhookDto.TenantId"/>
/// se confirma y el llamador entra en <c>AmbitoTenantExplicito</c>.
/// </summary>
public class WebhookTenantResolver(CaeManagerDbContext dbContext) : IWebhookTenantResolver
{
    public async Task<VerificacionWebhookDto> VerificarAsync(
        Guid conexionIntegracionId, string clientStateRecibido, string? subscriptionIdRecibido, CancellationToken cancellationToken)
    {
        var fila = await dbContext.SuscripcionesWebhook
            .IgnoreQueryFilters()
            .Where(s => s.ConexionIntegracionId == conexionIntegracionId)
            .Select(s => new { s.TenantId, s.ClientState, s.GraphSubscriptionId, s.FechaExpiracionUtc })
            .FirstOrDefaultAsync(cancellationToken);

        // Caducidad (auditoría módulo 6): una notificación puede llegar tras
        // vencer la suscripción local por desfase de reloj o por un fallo de
        // renovación — Graph todavía la considerara viva unos minutos más.
        // Sin esto, clientState + subscriptionId de una suscripción ya
        // caducada localmente se seguían aceptando indefinidamente.
        if (fila is null || fila.ClientState != clientStateRecibido ||
            string.IsNullOrWhiteSpace(subscriptionIdRecibido) || fila.GraphSubscriptionId != subscriptionIdRecibido ||
            fila.FechaExpiracionUtc <= DateTime.UtcNow)
        {
            return new VerificacionWebhookDto(Verificado: false, TenantId: null);
        }

        return new VerificacionWebhookDto(Verificado: true, fila.TenantId);
    }
}
