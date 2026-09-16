using CaeManager.Application.Common;
using MediatR;

namespace CaeManager.Application.Tenants.Queries.EsTenantOrigenPlataforma;

/// <summary>
/// Expone al Web si el tenant de ORIGEN del usuario actual es la organización
/// marcada <c>Tenant.EsPlataforma</c> — mismo criterio, mismo repositorio, que
/// <c>AbrirAccesoSoporteCommand</c>/<c>CerrarAccesoSoporteCommand</c> comprueban
/// antes de dejar abrir o cerrar una ventana de acceso de soporte
/// (<see cref="CaeManager.Application.Tenants.AutorizacionAccesoSoporte"/>).
///
/// <para>
/// Es UX, no enforcement — mismo criterio que <c>EsAdministradorPlataformaQuery</c>:
/// la seguridad vive en el comando. La otra mitad del predicado de esos dos
/// comandos —que el tenant de origen sea además la Consultora de la delegación
/// concreta— no hace falta consultarla aquí: ya viaja en
/// <c>DelegacionDto.SomosLaConsultora</c>, calculada en <c>ObtenerDelegacionesQuery</c>.
/// </para>
/// </summary>
public record EsTenantOrigenPlataformaQuery : IRequest<bool>;

public class EsTenantOrigenPlataformaQueryHandler(ITenantsQueryContext dbContext, ICurrentUserService currentUserService)
    : IRequestHandler<EsTenantOrigenPlataformaQuery, bool>
{
    public async Task<bool> Handle(EsTenantOrigenPlataformaQuery request, CancellationToken cancellationToken)
    {
        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();
        return await AutorizacionAccesoSoporte.EsTenantOrigenPlataformaAsync(tenantOrigenId, dbContext, cancellationToken);
    }
}
