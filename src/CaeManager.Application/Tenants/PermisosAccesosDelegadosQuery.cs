using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using MediatR;

namespace CaeManager.Application.Tenants;

public sealed record PermisosAccesoDelegadoDto(bool PuedeRevocar, bool PuedeReactivar);

public sealed record PermisosAccesosDelegadosQuery(IReadOnlyCollection<Guid> Ids)
    : IRequest<IReadOnlyDictionary<Guid, PermisosAccesoDelegadoDto>>;

public sealed class PermisosAccesosDelegadosQueryHandler(
    ITenantsQueryContext tenantsContext, ICurrentUserService currentUserService,
    IDirectorioUsuariosService directorioUsuarios, ICatalogoIncorporacionCartera catalogo)
    : IRequestHandler<PermisosAccesosDelegadosQuery, IReadOnlyDictionary<Guid, PermisosAccesoDelegadoDto>>
{
    public async Task<IReadOnlyDictionary<Guid, PermisosAccesoDelegadoDto>> Handle(
        PermisosAccesosDelegadosQuery request, CancellationToken cancellationToken)
    {
        if (request.Ids.Count == 0)
            return new Dictionary<Guid, PermisosAccesoDelegadoDto>();

        var ids = request.Ids.Distinct().ToArray();
        // Conserva los filtros EF y RLS de la lectura individual, en el Tenant activo.
        var accesos = await EstadoDeAccesoDelegado.LeerEnLoteAsync(tenantsContext, ids, cancellationToken);
        var autorizados = await EquipoDelOperadorCaeExterno.PuedeEnLoteAsync(
            accesos.Where(a => a.EsDeOperadorExterno)
                .Select(a => (a.OperadorTenantId, a.TenantPropietarioId)).ToArray(),
            currentUserService, directorioUsuarios, catalogo, cancellationToken);
        return accesos.ToDictionary(a => a.Id, a => a.Permisos(
            autorizados.Contains((a.OperadorTenantId, a.TenantPropietarioId))));
    }
}
