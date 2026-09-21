using CaeManager.Application.Integraciones;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Integraciones;

/// <summary>
/// <c>IgnoreQueryFilters()</c> justificado (revisión explícita, ver
/// CLAUDE.md): en este punto de la petición no hay ningún tenant resuelto
/// todavía — es exactamente lo que este método existe para resolver. Mismo
/// patrón que <see cref="WebhookTenantResolver"/> (Microsoft 365); el índice
/// único GLOBAL de <c>LineaWhatsApp.PhoneNumberId</c> garantiza que la línea
/// resuelve a un único tenant. El filtro se retoma en cuanto el llamador
/// entra en <c>AmbitoTenantExplicito</c> con el TenantId devuelto.
///
/// <para>
/// <c>LineasWhatsApp</c> tiene RLS con FORCE
/// (<c>20260804004307_HabilitarRlsWhatsApp</c>): <c>IgnoreQueryFilters()</c>
/// quita el filtro de EF pero no esa política, así que bajo
/// <c>cae_app_runtime</c> esta consulta puede estar en la misma situación que
/// tenía <c>ApiKeyAuthenticationHandler.ObtenerPorHashAsync</c> antes de la
/// migración 20260921155801_ResolucionDeClaveApiBajoRls — sin medir, fuera
/// de alcance de esa corrección.
/// </para>
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
