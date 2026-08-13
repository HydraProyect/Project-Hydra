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
///
/// La memoización cubre los seis alcances, no solo el de Cliente. Antes solo
/// estaba el de Cliente y el resto se recalculaba cada vez, con el agravante
/// de que se llaman en cascada: Trabajador pide Centro, Vehículo pide Empresa
/// y Subcontrata, y Subcontrata vuelve a pedir Empresa. Una sola carga del
/// listado de Documentos —que pide cuatro alcances— repetía la consulta de
/// Empresas tres veces.
/// </summary>
public class AlcanceDatosService(CaeManagerDbContext dbContext, ICurrentUserService currentUserService) : IAlcanceDatosService
{
    private bool? _accesoTotal;
    private IReadOnlyList<Guid>? _clienteIds;
    private bool _clienteIdsResueltos;

    // Un flag aparte por alcance y no un "is not null": null es un valor con
    // significado propio (sin restricción), distinto de "todavía sin
    // resolver". Confundirlos convertiría el caché en un fallo abierto.
    private IReadOnlyList<Guid>? _centroIds;
    private bool _centroIdsResueltos;
    private IReadOnlyList<Guid>? _empresaIds;
    private bool _empresaIdsResueltos;
    private IReadOnlyList<Guid>? _subcontrataIds;
    private bool _subcontrataIdsResueltos;
    private IReadOnlyList<Guid>? _trabajadorIds;
    private bool _trabajadorIdsResueltos;
    private IReadOnlyList<Guid>? _vehiculoIds;
    private bool _vehiculoIdsResueltos;

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
        if (_centroIdsResueltos) return _centroIds;

        var clienteIds = await ObtenerClienteIdsVisiblesAsync(cancellationToken);

        _centroIds = clienteIds switch
        {
            null => null,
            { Count: 0 } => [],
            _ => await dbContext.Centros
                .Where(c => clienteIds.Contains(c.ClienteId))
                .Select(c => c.Id)
                .ToListAsync(cancellationToken)
        };
        _centroIdsResueltos = true;

        return _centroIds;
    }

    public async Task<IReadOnlyList<Guid>?> ObtenerEmpresaIdsVisiblesAsync(CancellationToken cancellationToken = default)
    {
        if (_empresaIdsResueltos) return _empresaIds;

        var clienteIds = await ObtenerClienteIdsVisiblesAsync(cancellationToken);

        if (clienteIds is null || clienteIds.Count == 0)
        {
            _empresaIds = clienteIds is null ? null : [];
            _empresaIdsResueltos = true;
            return _empresaIds;
        }

        var porCentro = dbContext.Centros.Where(c => clienteIds.Contains(c.ClienteId)).Select(c => c.EmpresaId);
        var porVinculoDirecto = dbContext.EmpresasClientes.Where(ec => clienteIds.Contains(ec.ClienteId)).Select(ec => ec.EmpresaId);

        _empresaIds = await porCentro.Concat(porVinculoDirecto).Distinct().ToListAsync(cancellationToken);
        _empresaIdsResueltos = true;

        return _empresaIds;
    }

    public async Task<IReadOnlyList<Guid>?> ObtenerSubcontrataIdsVisiblesAsync(CancellationToken cancellationToken = default)
    {
        if (_subcontrataIdsResueltos) return _subcontrataIds;

        var clienteIds = await ObtenerClienteIdsVisiblesAsync(cancellationToken);

        if (clienteIds is null || clienteIds.Count == 0)
        {
            _subcontrataIds = clienteIds is null ? null : [];
            _subcontrataIdsResueltos = true;
            return _subcontrataIds;
        }

        var empresaIds = await ObtenerEmpresaIdsVisiblesAsync(cancellationToken) ?? [];

        var porCliente = dbContext.SubcontratasClientes.Where(sc => clienteIds.Contains(sc.ClienteId)).Select(sc => sc.SubcontrataId);
        var porEmpresa = dbContext.SubcontratasEmpresas.Where(se => empresaIds.Contains(se.EmpresaId)).Select(se => se.SubcontrataId);

        _subcontrataIds = await porCliente.Concat(porEmpresa).Distinct().ToListAsync(cancellationToken);
        _subcontrataIdsResueltos = true;

        return _subcontrataIds;
    }

    public async Task<IReadOnlyList<Guid>?> ObtenerTrabajadorIdsVisiblesAsync(CancellationToken cancellationToken = default)
    {
        if (_trabajadorIdsResueltos) return _trabajadorIds;

        var centroIds = await ObtenerCentroIdsVisiblesAsync(cancellationToken);

        _trabajadorIds = centroIds switch
        {
            null => null,
            { Count: 0 } => [],
            _ => await dbContext.Asignaciones
                .Where(a => centroIds.Contains(a.CentroId) && a.FechaBaja == null)
                .Select(a => a.TrabajadorId)
                .Distinct()
                .ToListAsync(cancellationToken)
        };
        _trabajadorIdsResueltos = true;

        return _trabajadorIds;
    }

    public async Task<IReadOnlyList<Guid>?> ObtenerVehiculoIdsVisiblesAsync(CancellationToken cancellationToken = default)
    {
        if (_vehiculoIdsResueltos) return _vehiculoIds;

        var empresaIds = await ObtenerEmpresaIdsVisiblesAsync(cancellationToken);
        if (empresaIds is null)
        {
            _vehiculoIdsResueltos = true;
            return _vehiculoIds = null;
        }

        var subcontrataIds = await ObtenerSubcontrataIdsVisiblesAsync(cancellationToken) ?? [];

        _vehiculoIds = empresaIds.Count == 0 && subcontrataIds.Count == 0
            ? []
            : await dbContext.Vehiculos
                .Where(v =>
                    (v.EmpresaId != null && empresaIds.Contains(v.EmpresaId.Value)) ||
                    (v.SubcontrataId != null && subcontrataIds.Contains(v.SubcontrataId.Value)))
                .Select(v => v.Id)
                .ToListAsync(cancellationToken);
        _vehiculoIdsResueltos = true;

        return _vehiculoIds;
    }

    /// <summary>
    /// Sin memoización por diseño: a diferencia de los seis alcances de
    /// arriba (un único valor por request, reutilizado por varios filtros de
    /// una misma Query), esto se llama con un Id distinto cada vez —
    /// memoizar por Id sería un diccionario para un método que ya resuelve
    /// con una única consulta indexada por clave primaria.
    /// </summary>
    public async Task<bool> ConexionIntegracionVisibleAsync(Guid conexionIntegracionId, CancellationToken cancellationToken = default)
    {
        var propietarioId = await dbContext.ConexionesIntegracion
            .Where(c => c.Id == conexionIntegracionId)
            .Select(c => c.GestorPropietarioId)
            .FirstOrDefaultAsync(cancellationToken);

        if (propietarioId is null) return true;

        var usuarioActualId = await currentUserService.ObtenerUsuarioActualIdAsync();
        return propietarioId == usuarioActualId;
    }
}
