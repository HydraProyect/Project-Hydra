using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Plataforma;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants.Queries.ObtenerLogoTenant;

/// <param name="ArchivoClave">Clave del blob, solo para el endpoint que lo sirve.</param>
/// <param name="Version">Hash corto del PNG, para <c>?v=</c>; nunca autoriza nada.</param>
public sealed record LogoTenantDto(string ArchivoClave, string Version);

/// <summary>
/// El logo de un Tenant, si el usuario actual alcanza ese Tenant (contrato del selector de Tenant,
/// § 4.1.5 e invariante I11). Null en los tres casos negativos —Tenant no autorizado, inexistente o
/// sin logo—, indistinguibles para quien llama. La autorización se decide ANTES de leer
/// <c>Tenants</c>: con un Tenant no autorizado, exista o no, la fila no se consulta.
/// </summary>
public record ObtenerLogoTenantQuery(Guid TenantId) : IRequest<LogoTenantDto?>;

public class ObtenerLogoTenantQueryHandler(
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    ICurrentUserService currentUserService,
    IOperacionesQueryContext operaciones,
    ITenantsQueryContext tenants)
    : IRequestHandler<ObtenerLogoTenantQuery, LogoTenantDto?>
{
    public async Task<LogoTenantDto?> Handle(ObtenerLogoTenantQuery request, CancellationToken cancellationToken)
    {
        if (!await AutorizadoAsync(request.TenantId, cancellationToken))
            return null;

        return await tenants.Tenants
            .Where(t => t.Id == request.TenantId && t.LogoArchivoClave != null && t.LogoVersion != null)
            .Select(t => new LogoTenantDto(t.LogoArchivoClave!, t.LogoVersion!))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<bool> AutorizadoAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty) return false;

        // En Sesión Privilegiada el único Tenant alcanzable es el objetivo de la sesión (I16), aunque
        // el usuario de plataforma tenga otras vías.
        if (await sesionPrivilegiadaActual.ObtenerAsync(cancellationToken) is { } sesion)
            return sesion.TenantObjetivoId == tenantId;

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();
        if (usuarioId is null || tenantOrigenId is null) return false;

        // El mismo predicado que la lista del selector y el POST de selección (lote 0, I2).
        return await TenantsBeneficiariosAutorizados.EstaAutorizadoAsync(
            operaciones, tenants, usuarioId.Value, tenantOrigenId.Value, tenantId,
            DateTime.UtcNow, cancellationToken);
    }
}
