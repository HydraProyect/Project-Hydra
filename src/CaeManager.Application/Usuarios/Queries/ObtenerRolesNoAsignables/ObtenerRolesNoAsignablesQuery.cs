using CaeManager.Application.Common;
using MediatR;

namespace CaeManager.Application.Usuarios.Queries.ObtenerRolesNoAsignables;

/// <summary>
/// Roles que el selector de /usuarios no debe ofrecer en el Context Workspace
/// activo: los de <see cref="RolesReservadosAlTenantDeOrigen"/> cuando el
/// contexto es cruzado, ninguno en el Tenant de origen.
///
/// <para>
/// Es UX, no enforcement — mismo criterio que <c>EsTenantOrigenPlataformaQuery</c>:
/// la autoridad es <c>VerificarRolAsignableQuery</c>, que se consulta al guardar.
/// </para>
/// </summary>
public record ObtenerRolesNoAsignablesQuery : IRequest<IReadOnlyList<string>>;

public class ObtenerRolesNoAsignablesQueryHandler(ICurrentUserService currentUserService, ITenantActual tenantActual)
    : IRequestHandler<ObtenerRolesNoAsignablesQuery, IReadOnlyList<string>>
{
    public async Task<IReadOnlyList<string>> Handle(ObtenerRolesNoAsignablesQuery request, CancellationToken cancellationToken)
    {
        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();

        return RolesReservadosAlTenantDeOrigen.EsContextoCruzado(tenantOrigenId, tenantActual.TenantId)
            ? RolesReservadosAlTenantDeOrigen.Roles
            : [];
    }
}
