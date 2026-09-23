using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.Usuarios.Queries.VerificarRolAsignable;

/// <summary>
/// Decide si el actor puede conceder <paramref name="Rol"/> a una cuenta del
/// Context Workspace activo, según <see cref="RolesReservadosAlTenantDeOrigen"/>.
/// Es la autoridad: /usuarios la consulta justo antes de escribir, en el alta
/// y en el cambio de rol, y no escribe si falla. Que el selector de rol oculte
/// esos roles (<c>ObtenerRolesNoAsignablesQuery</c>) es solo comodidad.
///
/// <para>
/// El Tenant de origen sale del claim firmado (<see cref="ICurrentUserService.ObtenerTenantOrigenIdAsync"/>),
/// y el Context Workspace, de <see cref="ITenantActual"/>: los dos mismos datos
/// con los que la página sella el <c>TenantId</c> de la cuenta nueva.
/// </para>
/// </summary>
public record VerificarRolAsignableQuery(string Rol) : IRequest<Result>;

public class VerificarRolAsignableQueryHandler(ICurrentUserService currentUserService, ITenantActual tenantActual)
    : IRequestHandler<VerificarRolAsignableQuery, Result>
{
    public async Task<Result> Handle(VerificarRolAsignableQuery request, CancellationToken cancellationToken)
    {
        if (!RolesReservadosAlTenantDeOrigen.EsReservado(request.Rol)) return Result.Exito();

        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();
        return RolesReservadosAlTenantDeOrigen.Verificar(request.Rol, tenantOrigenId, tenantActual.TenantId);
    }
}
