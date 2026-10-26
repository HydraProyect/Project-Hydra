using System.Security.Cryptography;
using System.Text;
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
        // Take(2), no FirstOrDefault: el índice único es (TenantId,
        // ConexionIntegracionId), así que la base admitiría dos filas de Tenants
        // distintos para la misma conexión. Si las hubiera, el Tenant resuelto
        // dependería del orden de la consulta; se rechaza en vez de elegir.
        var filas = await dbContext.SuscripcionesWebhook
            .IgnoreQueryFilters()
            .Where(s => s.ConexionIntegracionId == conexionIntegracionId)
            .Select(s => new { s.TenantId, s.ClientState, s.GraphSubscriptionId, s.FechaExpiracionUtc })
            .Take(2)
            .ToListAsync(cancellationToken);
        var fila = filas.Count == 1 ? filas[0] : null;

        // Caducidad (auditoría módulo 6): una notificación puede llegar tras
        // vencer la suscripción local por desfase de reloj o por un fallo de
        // renovación — Graph todavía la considerara viva unos minutos más.
        // Sin esto, clientState + subscriptionId de una suscripción ya
        // caducada localmente se seguían aceptando indefinidamente.
        // Un ClientState vacío en la fila (dato heredado o corrupto) también
        // rechaza: FixedTimeEquals de dos secuencias vacías daría true.
        if (fila is null || string.IsNullOrWhiteSpace(clientStateRecibido) ||
            string.IsNullOrWhiteSpace(fila.ClientState) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(fila.ClientState), Encoding.UTF8.GetBytes(clientStateRecibido)) ||
            string.IsNullOrWhiteSpace(subscriptionIdRecibido) || fila.GraphSubscriptionId != subscriptionIdRecibido ||
            fila.FechaExpiracionUtc <= DateTime.UtcNow)
        {
            return new VerificacionWebhookDto(Verificado: false, TenantId: null);
        }

        return new VerificacionWebhookDto(Verificado: true, fila.TenantId);
    }
}
