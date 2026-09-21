using CaeManager.Application.Integraciones;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Integraciones;

/// <summary>
/// <c>IgnoreQueryFilters()</c> justificado (revisión explícita, ver
/// CLAUDE.md): en este punto de la petición no hay ningún tenant resuelto
/// todavía —es exactamente lo que este método existe para resolver—, así
/// que el filtro global de tenant no puede estar activo. El filtro se retoma
/// en cuanto <see cref="VerificacionWebhookDto.TenantId"/> se confirma y el
/// llamador entra en <c>AmbitoTenantExplicito</c>.
///
/// <para>
/// <c>SuscripcionesWebhook</c> tiene RLS con FORCE
/// (<c>20260802191958_HabilitarRlsIntegraciones</c>): <c>IgnoreQueryFilters()</c>
/// quita el filtro de EF pero no esa política, así que bajo
/// <c>cae_app_runtime</c> esta consulta puede estar en la misma situación que
/// tenía <c>ApiKeyAuthenticationHandler.ObtenerPorHashAsync</c> antes de la
/// migración 20260921155801_ResolucionDeClaveApiBajoRls — sin medir, fuera
/// de alcance de esa corrección.
/// </para>
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
