using CaeManager.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants;

/// <summary>
/// Único punto de verdad de si el tenant de ORIGEN del usuario actual es la
/// organización marcada <c>Tenant.EsPlataforma</c> — la mitad del criterio que
/// <c>AbrirAccesoSoporteCommand</c> y <c>CerrarAccesoSoporteCommand</c>
/// comprueban antes de dejar tocar la ventana de un acceso de soporte (la otra
/// mitad, que el tenant de origen sea además la Consultora de esa delegación
/// concreta, ya la expone <c>DelegacionDto.SomosLaConsultora</c> — no hace
/// falta repetirla aquí).
///
/// <para>
/// Se extrae para que <c>EsTenantOrigenPlataformaQuery</c> pueda ofrecérselo a
/// la vista sin reimplementar la consulta: mismo <see cref="ITenantsQueryContext"/>,
/// mismo campo, ninguna copia. Deliberadamente nombrada sin el término
/// "Delegacion": no hace falta para este predicado, y evitarlo mantiene sin
/// tocar el trinquete de <c>TerminologiaCanonicaTests</c> (§ 5 del contrato).
/// </para>
/// </summary>
internal static class AutorizacionAccesoSoporte
{
    internal static async Task<bool> EsTenantOrigenPlataformaAsync(
        Guid? tenantOrigenId, ITenantsQueryContext dbContext, CancellationToken cancellationToken) =>
        tenantOrigenId is not null && await dbContext.Tenants
            .AnyAsync(t => t.Id == tenantOrigenId.Value && t.EsPlataforma, cancellationToken);
}
