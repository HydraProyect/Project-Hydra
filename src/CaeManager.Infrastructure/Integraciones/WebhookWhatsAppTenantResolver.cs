using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Integraciones;

/// <summary>
/// Resuelve el Tenant propietario por PhoneNumberId y lee la conexión bajo RLS.
/// </summary>
public class WebhookWhatsAppTenantResolver(CaeManagerDbContext dbContext) : IWebhookWhatsAppTenantResolver
{
    public async Task<VerificacionWebhookWhatsAppDto> ResolverPorPhoneNumberIdAsync(
        string phoneNumberId, CancellationToken cancellationToken)
    {
        var tenantId = await dbContext.Database
            .SqlQuery<Guid?>($"SELECT app_tenant_de_webhook_whatsapp({phoneNumberId}) AS \"Value\"")
            .SingleAsync(cancellationToken);
        if (tenantId is null)
            return new VerificacionWebhookWhatsAppDto(Verificado: false, TenantId: null, ConexionIntegracionId: null);

        using var ambito = AmbitoTenantExplicito.Establecer(tenantId.Value);
        var fila = await dbContext.LineasWhatsApp
            .Where(l => l.TenantId == tenantId && l.PhoneNumberId == phoneNumberId)
            .Select(l => new { l.TenantId, l.ConexionIntegracionId })
            .FirstOrDefaultAsync(cancellationToken);

        return fila is null
            ? new VerificacionWebhookWhatsAppDto(Verificado: false, TenantId: null, ConexionIntegracionId: null)
            : new VerificacionWebhookWhatsAppDto(Verificado: true, fila.TenantId, fila.ConexionIntegracionId);
    }
}
