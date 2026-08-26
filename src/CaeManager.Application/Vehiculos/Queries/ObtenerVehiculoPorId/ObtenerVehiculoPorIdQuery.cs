using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Vehiculos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Vehiculos.Queries.ObtenerVehiculoPorId;

public record ObtenerVehiculoPorIdQuery(Guid Id) : IRequest<VehiculoDetalleDto?>;

public record VehiculoDetalleDto(
    Guid Id,
    Guid? EmpresaId,
    Guid? SubcontrataId,
    string EmpleadorNombre,
    string Nombre,
    string Modelo,
    string NumeroPlaca,
    Guid Version);

public class ObtenerVehiculoPorIdQueryHandler(IEmpresasQueryContext empresasContext, IVehiculosQueryContext vehiculosContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerVehiculoPorIdQuery, VehiculoDetalleDto?>
{
    public async Task<VehiculoDetalleDto?> Handle(ObtenerVehiculoPorIdQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.VehiculoVisibleAsync(request.Id, cancellationToken)) return null;

        var vehiculo = await vehiculosContext.Vehiculos
            .Where(v => v.Id == request.Id)
            .Select(v => new { v.Id, v.EmpresaId, v.SubcontrataId, v.Nombre, v.Modelo, v.NumeroPlaca, v.Version })
            .FirstOrDefaultAsync(cancellationToken);

        if (vehiculo is null) return null;

        var empleadorNombre = vehiculo.EmpresaId is not null
            ? await empresasContext.Empresas.Where(e => e.Id == vehiculo.EmpresaId).Select(e => e.RazonSocial).FirstAsync(cancellationToken)
            : await empresasContext.Empresas.Where(e => e.Id == vehiculo.SubcontrataId).Select(e => e.RazonSocial).FirstAsync(cancellationToken);

        return new VehiculoDetalleDto(
            vehiculo.Id, vehiculo.EmpresaId, vehiculo.SubcontrataId, empleadorNombre, vehiculo.Nombre, vehiculo.Modelo,
            vehiculo.NumeroPlaca, vehiculo.Version);
    }
}
