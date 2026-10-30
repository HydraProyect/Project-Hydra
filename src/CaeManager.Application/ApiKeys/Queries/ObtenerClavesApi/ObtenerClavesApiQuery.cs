using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Tenants;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.ApiKeys.Queries.ObtenerClavesApi;

/// <summary>
/// Claves de la API pública emitidas para el tenant cliente de una
/// delegación de soporte. Solo la plataforma las gestiona en v1 (a
/// diferencia de <c>ObtenerActividadSoporteQuery</c>, el cliente visitado no
/// las lista aquí) — ver GenerarClaveApiCommand.
/// </summary>
public record ObtenerClavesApiQuery(Guid DelegacionTenantId) : IRequest<IReadOnlyList<ClaveApiDto>>;

public record ClaveApiDto(
    Guid Id, string NombreDescriptivo, string PrefijoVisible, DateTime CreadaEnUtc, DateTime? UltimoUsoUtc, bool EstaActiva, DateTime CaducaEnUtc);

public class ObtenerClavesApiQueryHandler(IApiKeysQueryContext claveContext, ITenantsQueryContext dbContext, ICurrentUserService currentUserService)
    : IRequestHandler<ObtenerClavesApiQuery, IReadOnlyList<ClaveApiDto>>
{
    public async Task<IReadOnlyList<ClaveApiDto>> Handle(ObtenerClavesApiQuery request, CancellationToken cancellationToken)
    {
        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();
        if (tenantOrigenId is null) return [];

        var delegacion = await dbContext.DelegacionesTenant
            .Where(d => d.Id == request.DelegacionTenantId && d.Proposito == PropositoDelegacion.Soporte)
            .Select(d => new { d.TenantConsultoraId, d.TenantClienteId })
            .FirstOrDefaultAsync(cancellationToken);

        if (delegacion is null) return [];

        var esLaPlataforma = delegacion.TenantConsultoraId == tenantOrigenId.Value && await dbContext.Tenants
            .AnyAsync(t => t.Id == tenantOrigenId.Value && t.EsPlataforma, cancellationToken);

        if (!esLaPlataforma) return [];

        using var ambito = AmbitoTenantExplicito.Establecer(delegacion.TenantClienteId);

        // RevocadaEnUtc == null y no c.EstaActiva: una propiedad sin mapear obliga a EF a
        // materializar la entidad entera, HashClave incluida, y en Sesión Privilegiada la
        // conexión lleva cae_app_soporte, que solo puede leer las columnas sin el hash
        // (migración SoporteSinLecturaDeCredenciales).
        return await claveContext.ClavesApi
            .OrderByDescending(c => c.CreadoEnUtc)
            .Select(c => new ClaveApiDto(c.Id, c.NombreDescriptivo, c.PrefijoVisible, c.CreadoEnUtc, c.UltimoUsoUtc, c.RevocadaEnUtc == null, c.CaducaEnUtc))
            .ToListAsync(cancellationToken);
    }
}
