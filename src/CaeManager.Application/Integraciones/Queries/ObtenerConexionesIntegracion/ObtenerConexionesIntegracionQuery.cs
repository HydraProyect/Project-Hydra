using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Domain.Integraciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;

public record ObtenerConexionesIntegracionQuery : IRequest<IReadOnlyList<ConexionIntegracionListaDto>>;

public record ConexionIntegracionListaDto(
    Guid Id, string BuzonEmail, string Nombre, Guid? ClienteId, string? ClienteNombre, EstadoConexionIntegracion Estado,
    DateTime FechaConectadaUtc);

public class ObtenerConexionesIntegracionQueryHandler(
    IIntegracionesQueryContext integracionesContext, IClientesQueryContext clientesContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerConexionesIntegracionQuery, IReadOnlyList<ConexionIntegracionListaDto>>
{
    public async Task<IReadOnlyList<ConexionIntegracionListaDto>> Handle(
        ObtenerConexionesIntegracionQuery request, CancellationToken cancellationToken)
    {
        var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);

        var query =
            from conexion in integracionesContext.ConexionesIntegracion
            where clienteIdsVisibles == null || conexion.ClienteId == null || clienteIdsVisibles.Contains(conexion.ClienteId!.Value)
            join cliente in clientesContext.Clientes on conexion.ClienteId equals cliente.Id into clientesUnidos
            from cliente in clientesUnidos.DefaultIfEmpty()
            orderby conexion.FechaConectadaUtc descending
            select new ConexionIntegracionListaDto(
                conexion.Id, conexion.BuzonEmail, conexion.Nombre, conexion.ClienteId, cliente!.RazonSocial, conexion.Estado,
                conexion.FechaConectadaUtc);

        return await query.ToListAsync(cancellationToken);
    }
}
