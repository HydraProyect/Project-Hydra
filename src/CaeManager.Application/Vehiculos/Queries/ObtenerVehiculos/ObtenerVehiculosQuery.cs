using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Vehiculos;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Vehiculos.Queries.ObtenerVehiculos;

/// <summary>
/// <paramref name="EstadoDocumental"/> es el filtro de estado de la pantalla:
/// un Vehículo no tiene estado propio, se deriva del peor estado de vigencia
/// de sus Documentos (ver <see cref="ICalculoEstadoDocumentalService"/>).
/// </summary>
public record ObtenerVehiculosQuery(
    string? Busqueda, Guid? EmpresaId = null, Guid? SubcontrataId = null, int Pagina = 1, int TamanoPagina = 20,
    string? OrdenarPor = null, bool Descendente = false, string? EstadoDocumental = null)
    : IRequest<ResultadoPaginado<VehiculoListaDto>>;

public record VehiculoListaDto(
    Guid Id, string Nombre, string Modelo, string NumeroPlaca, string EmpleadorNombre,
    EstadoDocumento? EstadoDocumental = null);

public class ObtenerVehiculosQueryHandler(
    IEmpresasQueryContext empresasContext,
    IVehiculosQueryContext vehiculosContext, IAlcanceDatosService alcanceDatos,
    ICalculoEstadoDocumentalService calculoEstadoDocumental)
    : IRequestHandler<ObtenerVehiculosQuery, ResultadoPaginado<VehiculoListaDto>>
{
    public async Task<ResultadoPaginado<VehiculoListaDto>> Handle(
        ObtenerVehiculosQuery request, CancellationToken cancellationToken)
    {
        var consulta =
            from vehiculo in vehiculosContext.Vehiculos
            join empresa in empresasContext.Empresas on vehiculo.EmpresaId equals empresa.Id into empresasCoincidentes
            from empresa in empresasCoincidentes.DefaultIfEmpty()
            join subcontrata in empresasContext.Empresas on vehiculo.SubcontrataId equals subcontrata.Id into subcontratasCoincidentes
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

        // Filtrar u ordenar por el estado documental obliga a conocerlo de
        // todas las filas antes de paginar — ver ObtenerCentrosQuery.
        var necesitaEstadoCompleto =
            !string.IsNullOrWhiteSpace(request.EstadoDocumental) ||
            string.Equals(request.OrdenarPor, nameof(VehiculoListaDto.EstadoDocumental), StringComparison.Ordinal);

        if (necesitaEstadoCompleto)
        {
            var todos = await consulta
                .Select(x => new VehiculoListaDto(
                    x.vehiculo.Id, x.vehiculo.Nombre, x.vehiculo.Modelo, x.vehiculo.NumeroPlaca, x.EmpleadorNombre))
                .ToListAsync(cancellationToken);

            var estadosTodos = await calculoEstadoDocumental.CalcularPeorEstadoAsync(
                AmbitoAplicacion.Vehiculo, todos.Select(v => v.Id).ToList(), cancellationToken);

            var conEstado = todos
                .Select(v => v with { EstadoDocumental = estadosTodos.GetValueOrDefault(v.Id) })
                .Where(v => EstadoDocumentalFiltro.Coincide(v.EstadoDocumental, request.EstadoDocumental));

            var ordenados = (request.Descendente
                    ? conEstado.OrderByDescending(v => EstadoDocumentalFiltro.ClaveOrden(v.EstadoDocumental))
                    : conEstado.OrderBy(v => EstadoDocumentalFiltro.ClaveOrden(v.EstadoDocumental)))
                .ThenBy(v => v.Nombre).ThenBy(v => v.Id)
                .ToList();

            return new ResultadoPaginado<VehiculoListaDto>(
                ordenados.Skip((request.Pagina - 1) * request.TamanoPagina).Take(request.TamanoPagina).ToList(),
                ordenados.Count, request.Pagina, request.TamanoPagina);
        }

        var total = await consulta.CountAsync(cancellationToken);

        // Lista blanca de columnas ordenables — ver ObtenerClientesQuery.
        var ordenada = (request.OrdenarPor, request.Descendente) switch
        {
            (nameof(VehiculoListaDto.Nombre), true) => consulta.OrderByDescending(x => x.vehiculo.Nombre),
            (nameof(VehiculoListaDto.Modelo), false) => consulta.OrderBy(x => x.vehiculo.Modelo).ThenBy(x => x.vehiculo.Nombre),
            (nameof(VehiculoListaDto.Modelo), true) => consulta.OrderByDescending(x => x.vehiculo.Modelo).ThenBy(x => x.vehiculo.Nombre),
            (nameof(VehiculoListaDto.NumeroPlaca), false) => consulta.OrderBy(x => x.vehiculo.NumeroPlaca),
            (nameof(VehiculoListaDto.NumeroPlaca), true) => consulta.OrderByDescending(x => x.vehiculo.NumeroPlaca),
            (nameof(VehiculoListaDto.EmpleadorNombre), false) => consulta.OrderBy(x => x.EmpleadorNombre).ThenBy(x => x.vehiculo.Nombre),
            (nameof(VehiculoListaDto.EmpleadorNombre), true) => consulta.OrderByDescending(x => x.EmpleadorNombre).ThenBy(x => x.vehiculo.Nombre),
            _ => consulta.OrderBy(x => x.vehiculo.Nombre)
        };
        // Desempate estable: sin un criterio total, PostgreSQL puede devolver
        // las filas empatadas en distinto orden entre una página y otra, y al
        // paginar en SQL eso hace que una fila aparezca dos veces o no
        // aparezca nunca. El Id no se ordena nunca por sí solo — solo cierra
        // el orden que haya elegido el usuario.
        ordenada = ordenada.ThenBy(x => x.vehiculo.Id);

        var elementos = await ordenada
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(x => new VehiculoListaDto(x.vehiculo.Id, x.vehiculo.Nombre, x.vehiculo.Modelo, x.vehiculo.NumeroPlaca, x.EmpleadorNombre))
            .ToListAsync(cancellationToken);

        // Solo para los de la página: el badge de la tabla, no un filtro.
        var estados = await calculoEstadoDocumental.CalcularPeorEstadoAsync(
            AmbitoAplicacion.Vehiculo, elementos.Select(v => v.Id).ToList(), cancellationToken);

        return new ResultadoPaginado<VehiculoListaDto>(
            elementos.Select(v => v with { EstadoDocumental = estados.GetValueOrDefault(v.Id) }).ToList(),
            total, request.Pagina, request.TamanoPagina);
    }
}
