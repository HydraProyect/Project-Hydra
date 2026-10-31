using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Configuracion.Queries;

public record ObtenerSeguridadAccesoQuery : IRequest<SeguridadAccesoDto>;

/// <summary><see cref="PuedeEditar"/> solo decide si la pantalla ofrece el ajuste; quien autoriza es el comando.</summary>
public record SeguridadAccesoDto(int MinutosInactividadSesion, bool ExigeSegundoFactorTodosLosPerfiles, bool PuedeEditar);

public class ObtenerSeguridadAccesoQueryHandler(
    IConfiguracionQueryContext dbContext,
    ITenantActual tenantActual,
    ICurrentUserService currentUserService,
    IAdministradorDelTenantPropietario administradorDelTenant,
    ITenantsQueryContext tenants)
    : IRequestHandler<ObtenerSeguridadAccesoQuery, SeguridadAccesoDto>
{
    public async Task<SeguridadAccesoDto> Handle(ObtenerSeguridadAccesoQuery request, CancellationToken cancellationToken)
    {
        var parametros = await dbContext.ParametrosSistema.SingleAsync(cancellationToken);
        return new SeguridadAccesoDto(
            parametros.MinutosInactividadSesion,
            parametros.ExigeSegundoFactorTodosLosPerfiles,
            await AutoridadSeguridadAcceso.PuedeEditarAsync(
                tenantActual, currentUserService, administradorDelTenant, tenants, cancellationToken));
    }
}
