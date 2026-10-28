using System.Security.Cryptography;
using System.Text;
using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Integraciones;

/// <summary>
/// Resuelve el Tenant propietario mediante una función acotada y comprueba la
/// suscripción dentro de su ámbito, antes de devolver el Tenant al llamador.
/// </summary>
public class WebhookTenantResolver(CaeManagerDbContext dbContext) : IWebhookTenantResolver
{
    public async Task<VerificacionWebhookDto> VerificarAsync(
        Guid conexionIntegracionId, string clientStateRecibido, string? subscriptionIdRecibido, CancellationToken cancellationToken)
    {
        var tenantId = await dbContext.Database
            .SqlQuery<Guid?>($"SELECT app_tenant_de_webhook_graph({conexionIntegracionId}) AS \"Value\"")
            .SingleAsync(cancellationToken);
        if (tenantId is null)
            return new VerificacionWebhookDto(Verificado: false, TenantId: null);

        using var ambito = AmbitoTenantExplicito.Establecer(tenantId.Value);
        var fila = await dbContext.SuscripcionesWebhook
            .Where(s => s.TenantId == tenantId && s.ConexionIntegracionId == conexionIntegracionId)
            .Select(s => new { s.TenantId, s.ClientState, s.GraphSubscriptionId, s.FechaExpiracionUtc })
            .SingleOrDefaultAsync(cancellationToken);

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
