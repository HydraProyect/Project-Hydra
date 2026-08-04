using CaeManager.Application.Integraciones;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Integraciones;

/// <summary>
/// <c>IgnoreQueryFilters()</c> justificado (revisión explícita, ver
/// CLAUDE.md): en este punto de la petición no hay ningún tenant resuelto
/// todavía — es exactamente lo que este método existe para resolver. Mismo
/// patrón que <see cref="WebhookTenantResolver"/> (Microsoft 365) y
/// <c>ApiKeyAuthenticationHandler.ObtenerPorHashAsync</c>; el índice único
/// GLOBAL de <c>LineaWhatsApp.PhoneNumberId</c> garantiza que la línea
/// resuelve a un único tenant. El filtro se retoma en cuanto el llamador
/// entra en <c>AmbitoTenantExplicito</c> con el TenantId devuelto.
/// </summary>
public class WebhookWhatsAppTenantResolver(CaeManagerDbContext dbContext) : IWebhookWhatsAppTenantResolver
{
    public async Task<VerificacionWebhookWhatsAppDto> ResolverPorPhoneNumberIdAsync(
        string phoneNumberId, CancellationToken cancellationToken)
    {
        var fila = await dbContext.LineasWhatsApp
            .IgnoreQueryFilters()
            .Where(l => l.PhoneNumberId == phoneNumberId)
            .Select(l => new { l.TenantId, l.ConexionIntegracionId })
            .FirstOrDefaultAsync(cancellationToken);

        return fila is null
            ? new VerificacionWebhookWhatsAppDto(Verificado: false, TenantId: null, ConexionIntegracionId: null)
            : new VerificacionWebhookWhatsAppDto(Verificado: true, fila.TenantId, fila.ConexionIntegracionId);
    }
}
