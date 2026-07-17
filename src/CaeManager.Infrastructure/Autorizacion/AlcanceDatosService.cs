using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Autorizacion;

/// <summary>
/// Implementación real de IAlcanceDatosService — vive en Infrastructure
/// porque necesita leer ApplicationUser (CoordinadorUsuarioId/ClienteId),
/// que Application no puede referenciar (ver Roles.cs). Cachea el resultado
/// de cada método en la propia instancia (scoped por request/circuito) para
/// no repetir la misma resolución de cartera varias veces en la misma
/// petición cuando varios filtros de una Query la necesitan.
/// </summary>
public class AlcanceDatosService(CaeManagerDbContext dbContext, ICurrentUserService currentUserService) : IAlcanceDatosService
{
    private bool? _accesoTotal;
    private IReadOnlyList<Guid>? _clienteIds;
    private bool _clienteIdsResueltos;

    public async Task<bool> TieneAccesoTotalAsync(CancellationToken cancellationToken = default)
    {
        if (_accesoTotal is not null) return _accesoTotal.Value;

        var rol = await currentUserService.ObtenerRolActualAsync();
        _accesoTotal = rol is Roles.Administrador or Roles.DireccionCae or Roles.Consulta;

        return _accesoTotal.Value;
    }

    public async Task<IReadOnlyList<Guid>?> ObtenerClienteIdsVisiblesAsync(CancellationToken cancellationToken = default)
    {
        if (_clienteIdsResueltos) return _clienteIds;

        if (await TieneAccesoTotalAsync(cancellationToken))
        {
            _clienteIds = null;
            _clienteIdsResueltos = true;
            return null;
        }

        var rol = await currentUserService.ObtenerRolActualAsync();
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();

        _clienteIds = (rol, usuarioId) switch
        {
            (Roles.Cliente, { } id) => await ObtenerClienteIdsParaRolClienteAsync(id, cancellationToken),
            (Roles.GestorCae, { } id) => await dbContext.Clientes
                .Where(c => c.EjecutivoUsuarioId == id)
                .Select(c => c.Id)
                .ToListAsync(cancellationToken),
            (Roles.CoordinadorCae, { } id) => await ObtenerClienteIdsParaCoordinadorAsync(id, cancellationToken),
            _ => []
        };
        _clienteIdsResueltos = true;

        return _clienteIds;
    }

    private async Task<IReadOnlyList<Guid>> ObtenerClienteIdsParaRolClienteAsync(Guid usuarioId, CancellationToken cancellationToken)
    {
        var clienteId = await dbContext.Users
            .Where(u => u.Id == usuarioId)
            .Select(u => u.ClienteId)
            .FirstOrDefaultAsync(cancellationToken);

        return clienteId is { } id ? [id] : [];
    }

    private async Task<IReadOnlyList<Guid>> ObtenerClienteIdsParaCoordinadorAsync(Guid coordinadorUsuarioId, CancellationToken cancellationToken)
    {
        var gestorIds = await dbContext.Users
            .Where(u => u.CoordinadorUsuarioId == coordinadorUsuarioId)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

        if (gestorIds.Count == 0) return [];

        return await dbContext.Clientes
            .Where(c => c.EjecutivoUsuarioId != null && gestorIds.Contains(c.EjecutivoUsuarioId!.Value))
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>?> ObtenerCentroIdsVisiblesAsync(CancellationToken cancellationToken = default)
    {
        var clienteIds = await ObtenerClienteIdsVisiblesAsync(cancellationToken);
        if (clienteIds is null) return null;
        if (clienteIds.Count == 0) return [];

        return await dbContext.Centros
            .Where(c => clienteIds.Contains(c.ClienteId))
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>?> ObtenerEmpresaIdsVisiblesAsync(CancellationToken cancellationToken = default)
    {
        var clienteIds = await ObtenerClienteIdsVisiblesAsync(cancellationToken);
        if (clienteIds is null) return null;
        if (clienteIds.Count == 0) return [];

        var porCentro = dbContext.Centros.Where(c => clienteIds.Contains(c.ClienteId)).Select(c => c.EmpresaId);
        var porVinculoDirecto = dbContext.EmpresasClientes.Where(ec => clienteIds.Contains(ec.ClienteId)).Select(ec => ec.EmpresaId);

        return await porCentro.Concat(porVinculoDirecto).Distinct().ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>?> ObtenerSubcontrataIdsVisiblesAsync(CancellationToken cancellationToken = default)
    {
        var clienteIds = await ObtenerClienteIdsVisiblesAsync(cancellationToken);
        if (clienteIds is null) return null;
        if (clienteIds.Count == 0) return [];

        var empresaIds = await ObtenerEmpresaIdsVisiblesAsync(cancellationToken) ?? [];

        var porCliente = dbContext.SubcontratasClientes.Where(sc => clienteIds.Contains(sc.ClienteId)).Select(sc => sc.SubcontrataId);
        var porEmpresa = dbContext.SubcontratasEmpresas.Where(se => empresaIds.Contains(se.EmpresaId)).Select(se => se.SubcontrataId);

        return await porCliente.Concat(porEmpresa).Distinct().ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>?> ObtenerTrabajadorIdsVisiblesAsync(CancellationToken cancellationToken = default)
    {
        var centroIds = await ObtenerCentroIdsVisiblesAsync(cancellationToken);
        if (centroIds is null) return null;
        if (centroIds.Count == 0) return [];

        return await dbContext.Asignaciones
            .Where(a => centroIds.Contains(a.CentroId) && a.FechaBaja == null)
            .Select(a => a.TrabajadorId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>?> ObtenerVehiculoIdsVisiblesAsync(CancellationToken cancellationToken = default)
    {
        var empresaIds = await ObtenerEmpresaIdsVisiblesAsync(cancellationToken);
        if (empresaIds is null) return null;

        var subcontrataIds = await ObtenerSubcontrataIdsVisiblesAsync(cancellationToken) ?? [];
        if (empresaIds.Count == 0 && subcontrataIds.Count == 0) return [];

        return await dbContext.Vehiculos
            .Where(v =>
                (v.EmpresaId != null && empresaIds.Contains(v.EmpresaId.Value)) ||
                (v.SubcontrataId != null && subcontrataIds.Contains(v.SubcontrataId.Value)))
            .Select(v => v.Id)
            .ToListAsync(cancellationToken);
    }
}
