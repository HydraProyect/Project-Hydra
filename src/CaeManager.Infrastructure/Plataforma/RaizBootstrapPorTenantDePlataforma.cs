using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Plataforma;

/// <inheritdoc cref="IRaizBootstrapPlataforma" />
/// <remarks>
/// <para>
/// La raíz es la pertenencia al tenant marcado como plataforma, comprobada
/// contra el tenant de <b>origen</b> del usuario. Es la única superficie que le
/// queda a <c>EsPlataforma</c> como autoridad: crear la concesión fundacional.
/// </para>
///
/// <para>
/// <b>Contra el origen y no contra <c>ITenantActual</c></b>, por el motivo de
/// siempre: el actual refleja el workspace activo y lo cambia la selección de
/// cliente; el de origen sale del claim de sesión.
/// </para>
///
/// <para>
/// <b>Qué no comprueba, y es deliberado:</b> sobre qué tenant se va a conceder.
/// Esa es <see cref="ReglaTenantObjetivoAjeno"/>, que el comando aplica aparte —
/// pertenecer a la raíz y elegir un objetivo legítimo son dos condiciones, y
/// fundirlas volvería a producir una autorización cuyo significado depende de
/// quién la invoque.
/// </para>
/// </remarks>
public class RaizBootstrapPorTenantDePlataforma(
    ITenantsQueryContext tenantsContext,
    ICurrentUserService currentUserService) : IRaizBootstrapPlataforma
{
    public async Task<bool> EsRaizDeConfianzaAsync(Guid usuarioId, CancellationToken cancellationToken = default)
    {
        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();
        if (tenantOrigenId is null) return false;

        return await tenantsContext.Tenants
            .AnyAsync(t => t.Id == tenantOrigenId.Value && t.EsPlataforma, cancellationToken);
    }
}
