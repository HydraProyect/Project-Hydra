using CaeManager.Application.Common;
using CaeManager.Application.Centros;
using CaeManager.Application.Incidencias;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Incidencias;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Incidencias.Queries.ObtenerIncidencias;

public record ObtenerIncidenciasQuery(string? Busqueda, bool SoloSinResolver, int Pagina = 1, int TamanoPagina = 20)
    : IRequest<ResultadoPaginado<IncidenciaListaDto>>;

public record IncidenciaListaDto(
    Guid Id, string CentroNombre, string? TrabajadorNombre, TipoIncidencia Tipo,
    GravedadIncidencia Gravedad, DateOnly FechaOcurrencia, bool Resuelta);

public class ObtenerIncidenciasQueryHandler(ICentrosQueryContext centrosContext, IIncidenciasQueryContext incidenciasContext, ITrabajadoresQueryContext trabajadoresContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerIncidenciasQuery, ResultadoPaginado<IncidenciaListaDto>>
{
    public async Task<ResultadoPaginado<IncidenciaListaDto>> Handle(
        ObtenerIncidenciasQuery request, CancellationToken cancellationToken)
    {
        var consulta =
            from incidencia in incidenciasContext.Incidencias
            join centro in centrosContext.Centros on incidencia.CentroId equals centro.Id
            join trabajador in trabajadoresContext.Trabajadores on incidencia.TrabajadorId equals trabajador.Id into trabajadores
            from trabajador in trabajadores.DefaultIfEmpty()
            select new { incidencia, centro, trabajador };

        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        if (centroIdsVisibles is not null)
            consulta = consulta.Where(x => centroIdsVisibles.Contains(x.centro.Id));

        if (request.SoloSinResolver)
            consulta = consulta.Where(x => !x.incidencia.Resuelta);

        if (!string.IsNullOrWhiteSpace(request.Busqueda))
        {
            var busqueda = request.Busqueda.ToUpper();
            consulta = consulta.Where(x =>
                x.centro.Nombre.ToUpper().Contains(busqueda) ||
                x.incidencia.Descripcion.ToUpper().Contains(busqueda) ||
                (x.trabajador != null && (x.trabajador.Nombre + " " + x.trabajador.Apellidos).ToUpper().Contains(busqueda)));
        }

        var total = await consulta.CountAsync(cancellationToken);

        var elementos = await consulta
            .OrderByDescending(x => x.incidencia.FechaOcurrencia)
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(x => new IncidenciaListaDto(
                x.incidencia.Id,
                x.centro.Nombre,
                x.trabajador == null ? null : x.trabajador.Nombre + " " + x.trabajador.Apellidos,
                x.incidencia.Tipo,
                x.incidencia.Gravedad,
                x.incidencia.FechaOcurrencia,
                x.incidencia.Resuelta))
            .ToListAsync(cancellationToken);

        return new ResultadoPaginado<IncidenciaListaDto>(elementos, total, request.Pagina, request.TamanoPagina);
    }
}
