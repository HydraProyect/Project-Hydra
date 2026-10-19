using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Tenants.Queries.ObtenerTenantObjetivoSesion;
using MediatR;

namespace CaeManager.Application.Dashboard.Queries;

/// <summary>
/// Los Tenants que recorre el Cuadro de dirección, según por qué puerta entró quien mira
/// (<see cref="AutorizacionCuadroDireccion"/>):
/// <list type="bullet">
/// <item>Por rol (Dirección CAE, Coordinador CAE, Administrador): los Tenants beneficiarios autorizados
/// (<see cref="ObtenerClientesAutorizadosQuery"/>), como hasta ahora.</item>
/// <item>Por Sesión Privilegiada de acceso total: solo el Tenant objetivo de la sesión
/// (<see cref="ObtenerTenantObjetivoSesionQuery"/>). El Actor de Plataforma TALVEG no tiene cartera ni Operador CAE, y
/// sus Tenants de origen no son lo que abrió: recorrerlos le enseñaría datos que la sesión no cubre, o ninguno.</item>
/// </list>
/// Fuera de las dos, ninguno (fallo cerrado). Cada handler sigue exigiendo antes la lectura.
/// </summary>
public record ObtenerTenantsCuadroDireccionQuery : IRequest<IReadOnlyList<ClienteAutorizadoDto>>;

public class ObtenerTenantsCuadroDireccionQueryHandler(IMediator mediator, ICurrentUserService currentUserService)
    : IRequestHandler<ObtenerTenantsCuadroDireccionQuery, IReadOnlyList<ClienteAutorizadoDto>>
{
    public async Task<IReadOnlyList<ClienteAutorizadoDto>> Handle(
        ObtenerTenantsCuadroDireccionQuery request, CancellationToken cancellationToken)
    {
        if (await currentUserService.ObtenerRolEfectivoAsync() is { } rol && AutorizacionCuadroDireccion.RolesQueLeen.Contains(rol))
            return await mediator.Send(new ObtenerClientesAutorizadosQuery(), cancellationToken);

        return await mediator.Send(new ObtenerTenantObjetivoSesionQuery(), cancellationToken) is { } objetivo ? [objetivo] : [];
    }
}
