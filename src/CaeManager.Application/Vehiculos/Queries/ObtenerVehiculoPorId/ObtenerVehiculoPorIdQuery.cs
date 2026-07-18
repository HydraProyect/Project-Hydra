using CaeManager.Application.Common;
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
    string NumeroPlaca);

public class ObtenerVehiculoPorIdQueryHandler(IApplicationDbContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerVehiculoPorIdQuery, VehiculoDetalleDto?>
{
    public async Task<VehiculoDetalleDto?> Handle(ObtenerVehiculoPorIdQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.VehiculoVisibleAsync(request.Id, cancellationToken)) return null;

        var vehiculo = await dbContext.Vehiculos
            .Where(v => v.Id == request.Id)
            .Select(v => new { v.Id, v.EmpresaId, v.SubcontrataId, v.Nombre, v.Modelo, v.NumeroPlaca })
            .FirstOrDefaultAsync(cancellationToken);

        if (vehiculo is null) return null;

        var empleadorNombre = vehiculo.EmpresaId is not null
            ? await dbContext.Empresas.Where(e => e.Id == vehiculo.EmpresaId).Select(e => e.RazonSocial).FirstAsync(cancellationToken)
            : await dbContext.Subcontratas.Where(s => s.Id == vehiculo.SubcontrataId).Select(s => s.RazonSocial).FirstAsync(cancellationToken);

        return new VehiculoDetalleDto(
            vehiculo.Id, vehiculo.EmpresaId, vehiculo.SubcontrataId, empleadorNombre, vehiculo.Nombre, vehiculo.Modelo, vehiculo.NumeroPlaca);
    }
}
