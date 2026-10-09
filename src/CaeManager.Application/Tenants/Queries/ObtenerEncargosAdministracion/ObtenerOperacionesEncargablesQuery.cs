using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Tenants;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants.Queries.ObtenerEncargosAdministracion;

/// <summary>
/// Los Operadores CAE externos del Tenant activo a los que hoy se puede
/// encargar su administración (decisión D-8, 2026-10-08): los que lo operan
/// entero, con la operación vigente y sin un encargo sin retirar. Es lo que
/// ofrece la pantalla que registra el encargo; quién puede registrarlo lo
/// vuelve a decidir <c>RegistrarEncargoAdministracionCommand</c>.
///
/// <para>
/// Misma puerta que la lista de encargos
/// (<see cref="AutoridadSobreElEncargo"/>): a quien no gobierna el encargo
/// —incluido quien administra por encargo— le devuelve una lista vacía.
/// </para>
/// </summary>
public record ObtenerOperacionesEncargablesQuery : IRequest<IReadOnlyList<OperacionEncargableDto>>;

public record OperacionEncargableDto(Guid AsignacionOperacionId, Guid OperadorTenantId, string OperadorNombre);

public class ObtenerOperacionesEncargablesQueryHandler(
    ITenantActual tenantActual,
    AutoridadSobreElEncargo autoridad,
    IEncargoAdministracionRepository encargos,
    ITenantsQueryContext tenantsContext,
    TimeProvider reloj)
    : IRequestHandler<ObtenerOperacionesEncargablesQuery, IReadOnlyList<OperacionEncargableDto>>
{
    public async Task<IReadOnlyList<OperacionEncargableDto>> Handle(
        ObtenerOperacionesEncargablesQuery request, CancellationToken cancellationToken)
    {
        // La autoridad va antes que cualquier lectura, igual que al registrar.
        if (tenantActual.TenantId is not { } propietarioTenantId
            || await autoridad.ResolverAsync(propietarioTenantId, cancellationToken) is null)
            return [];

        var operaciones = await encargos.ListarOperacionesEncargablesAsync(
            propietarioTenantId, reloj.GetUtcNow().UtcDateTime, cancellationToken);
        if (operaciones.Count == 0)
            return [];

        var operadorIds = operaciones.Select(o => o.OperadorTenantId).Distinct().ToList();
        var nombres = await tenantsContext.Tenants.AsNoTracking()
            .Where(t => operadorIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Nombre })
            .ToDictionaryAsync(t => t.Id, t => t.Nombre, cancellationToken);

        return operaciones
            .Where(o => nombres.ContainsKey(o.OperadorTenantId))
            .Select(o => new OperacionEncargableDto(o.Id, o.OperadorTenantId, nombres[o.OperadorTenantId]))
            .OrderBy(o => o.OperadorNombre, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
