using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;

/// <summary>
/// Los clientes que el usuario actual puede activar como Delegated Workspace
/// (ADR-004 § 6): siempre su propio tenant de origen, más los Clientes
/// Delegantes de cualquier <c>DelegacionTenant</c> activa donde tenga una
/// <c>AsignacionOperadorDelegado</c> vigente. Para un usuario que no es
/// Operador Delegado de nadie, el resultado tiene un único elemento — el
/// caso de hoy, sin cambios (ver <c>SelectorClienteActivo.razor</c>, que no
/// se muestra cuando esta lista tiene un solo elemento).
/// </summary>
public record ObtenerClientesAutorizadosQuery : IRequest<IReadOnlyList<ClienteAutorizadoDto>>;

public record ClienteAutorizadoDto(Guid TenantId, string Nombre, bool EsOrigen);

public class ObtenerClientesAutorizadosQueryHandler(IApplicationDbContext dbContext, ICurrentUserService currentUserService)
    : IRequestHandler<ObtenerClientesAutorizadosQuery, IReadOnlyList<ClienteAutorizadoDto>>
{
    public async Task<IReadOnlyList<ClienteAutorizadoDto>> Handle(
        ObtenerClientesAutorizadosQuery request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();

        if (usuarioId is null || tenantOrigenId is null)
            return [];

        var resultado = new List<ClienteAutorizadoDto>();

        var tenantOrigen = await dbContext.Tenants
            .Where(t => t.Id == tenantOrigenId.Value)
            .Select(t => new ClienteAutorizadoDto(t.Id, t.Nombre, true))
            .FirstOrDefaultAsync(cancellationToken);

        if (tenantOrigen is not null)
            resultado.Add(tenantOrigen);

        var delegados = await (
            from asignacion in dbContext.AsignacionesOperadorDelegado
            join delegacion in dbContext.DelegacionesTenant on asignacion.DelegacionTenantId equals delegacion.Id
            join tenant in dbContext.Tenants on delegacion.TenantClienteId equals tenant.Id
            where asignacion.UsuarioId == usuarioId.Value && delegacion.Activa
            orderby tenant.Nombre
            select new ClienteAutorizadoDto(tenant.Id, tenant.Nombre, false))
            .ToListAsync(cancellationToken);

        resultado.AddRange(delegados);
        return resultado;
    }
}
