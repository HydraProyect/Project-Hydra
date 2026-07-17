using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Vehiculos.Queries.ObtenerVehiculos;

public record ObtenerVehiculosQuery(
    string? Busqueda, Guid? EmpresaId = null, Guid? SubcontrataId = null, int Pagina = 1, int TamanoPagina = 20)
    : IRequest<ResultadoPaginado<VehiculoListaDto>>;

public record VehiculoListaDto(Guid Id, string Nombre, string Modelo, string NumeroPlaca, string EmpleadorNombre);

public class ObtenerVehiculosQueryHandler(IApplicationDbContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerVehiculosQuery, ResultadoPaginado<VehiculoListaDto>>
{
    public async Task<ResultadoPaginado<VehiculoListaDto>> Handle(
        ObtenerVehiculosQuery request, CancellationToken cancellationToken)
    {
        var consulta =
            from vehiculo in dbContext.Vehiculos
            join empresa in dbContext.Empresas on vehiculo.EmpresaId equals empresa.Id into empresasCoincidentes
            from empresa in empresasCoincidentes.DefaultIfEmpty()
            join subcontrata in dbContext.Subcontratas on vehiculo.SubcontrataId equals subcontrata.Id into subcontratasCoincidentes
            from subcontrata in subcontratasCoincidentes.DefaultIfEmpty()
            select new { vehiculo, EmpleadorNombre = empresa != null ? empresa.RazonSocial : subcontrata!.RazonSocial };

        // Este es el listado (tabla /vehiculos), no el selector — se acota a
        // los de una Empresa/Subcontrata visible (ver IAlcanceDatosService).
        var vehiculoIdsVisibles = await alcanceDatos.ObtenerVehiculoIdsVisiblesAsync(cancellationToken);
        if (vehiculoIdsVisibles is not null)
            consulta = consulta.Where(x => vehiculoIdsVisibles.Contains(x.vehiculo.Id));

        if (!string.IsNullOrWhiteSpace(request.Busqueda))
        {
            var busqueda = request.Busqueda.ToUpper();
            consulta = consulta.Where(x =>
                x.vehiculo.Nombre.ToUpper().Contains(busqueda) ||
                x.vehiculo.Modelo.ToUpper().Contains(busqueda) ||
                x.vehiculo.NumeroPlaca.ToUpper().Contains(busqueda));
        }

        if (request.EmpresaId is not null)
            consulta = consulta.Where(x => x.vehiculo.EmpresaId == request.EmpresaId);

        if (request.SubcontrataId is not null)
            consulta = consulta.Where(x => x.vehiculo.SubcontrataId == request.SubcontrataId);

        var total = await consulta.CountAsync(cancellationToken);

        var elementos = await consulta
            .OrderBy(x => x.vehiculo.Nombre)
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(x => new VehiculoListaDto(x.vehiculo.Id, x.vehiculo.Nombre, x.vehiculo.Modelo, x.vehiculo.NumeroPlaca, x.EmpleadorNombre))
            .ToListAsync(cancellationToken);

        return new ResultadoPaginado<VehiculoListaDto>(elementos, total, request.Pagina, request.TamanoPagina);
    }
}
