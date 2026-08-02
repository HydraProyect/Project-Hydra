using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants.Queries.EsAdministradorPlataforma;

/// <summary>
/// Expone al Web si el tenant de <b>origen</b> del usuario actual es el
/// tenant marcado como plataforma — mismo criterio de autorización que
/// <c>CrearClienteDeleganteCommand</c> y <c>AbrirAccesoSoporteCommand</c>,
/// aquí solo para decidir si Delegaciones.razor muestra el botón
/// "Nueva delegación" (v1 de ADR-004 § 12.2: solo administrador de plataforma).
/// </summary>
public record EsAdministradorPlataformaQuery : IRequest<bool>;

public class EsAdministradorPlataformaQueryHandler(ITenantsQueryContext tenantsContext, ICurrentUserService currentUserService)
    : IRequestHandler<EsAdministradorPlataformaQuery, bool>
{
    public async Task<bool> Handle(EsAdministradorPlataformaQuery request, CancellationToken cancellationToken)
    {
        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();
        if (tenantOrigenId is null) return false;

        return await tenantsContext.Tenants.AnyAsync(t => t.Id == tenantOrigenId.Value && t.EsPlataforma, cancellationToken);
    }
}
