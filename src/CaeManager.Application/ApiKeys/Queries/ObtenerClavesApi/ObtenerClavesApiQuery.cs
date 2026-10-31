using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Domain.ApiKeys;
using CaeManager.Domain.Tenants;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.ApiKeys.Queries.ObtenerClavesApi;

/// <summary>
/// Claves de la API pública emitidas para el Tenant propietario de una delegación de Soporte, en
/// la vista del Actor de Plataforma TALVEG. Exige la misma autoridad que
/// <see cref="Commands.RevocarClaveApi.RevocarClaveApiCommand"/>: concesión AdminPlataforma vigente
/// sobre ese Tenant y cuenta de la organización de origen de la delegación, nunca la pertenencia
/// a un Tenant marcado como plataforma (OWASP ASVS 4.0.3 V4.1.3). El Tenant propietario lee las
/// suyas con <see cref="ObtenerClavesApiDeMiOrganizacion.ObtenerClavesApiDeMiOrganizacionQuery"/>.
/// </summary>
public record ObtenerClavesApiQuery(Guid DelegacionTenantId) : IRequest<IReadOnlyList<ClaveApiDto>>;

/// <summary>
/// <paramref name="EstaActiva"/> es que no está revocada: decide si cabe revocarla.
/// <paramref name="Cortada"/> es que, sin estar revocada ni caducada, ya no autentica porque le
/// falta respaldo (<see cref="IClaveApiRepository.ClaveSigueRespaldadaAsync"/>).
/// </summary>
public record ClaveApiDto(
    Guid Id, string NombreDescriptivo, string PrefijoVisible, DateTime CreadaEnUtc, DateTime? UltimoUsoUtc, bool EstaActiva, DateTime CaducaEnUtc, bool Cortada = false);

public class ObtenerClavesApiQueryHandler(
    IApiKeysQueryContext claveContext,
    IClaveApiRepository claveRepositorio,
    ITenantsQueryContext dbContext,
    IAutorizacionAdminPlataforma autorizacion,
    ICurrentUserService currentUserService)
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

        if (delegacion is null || delegacion.TenantConsultoraId != tenantOrigenId.Value) return [];

        var tenantPropietarioId = delegacion.TenantClienteId;
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null
            || !await autorizacion.PuedeSobreTenantAsync(usuarioId.Value, tenantPropietarioId, cancellationToken))
            return [];

        using var ambito = AmbitoTenantExplicito.Establecer(tenantPropietarioId);

        return await ListadoClavesApi.LeerAsync(claveContext, claveRepositorio, tenantPropietarioId, cancellationToken);
    }
}
