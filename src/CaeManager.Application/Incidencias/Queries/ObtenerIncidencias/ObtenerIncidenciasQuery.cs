using CaeManager.Application.Common;
using CaeManager.Application.Centros;
using CaeManager.Application.Incidencias;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Incidencias;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Incidencias.Queries.ObtenerIncidencias;

/// <summary>
/// <paramref name="Resuelta"/> es el filtro de estado de la pantalla (una
/// Incidencia no tiene enum de estado: su estado es <c>Resuelta</c>).
/// <paramref name="SoloSinResolver"/> se mantiene porque ya lo usan la vista y
/// la API; cuando está activo, gana.
/// </summary>
public record ObtenerIncidenciasQuery(
    string? Busqueda, bool SoloSinResolver, int Pagina = 1, int TamanoPagina = 20,
    bool? Resuelta = null, string? OrdenarPor = null, bool Descendente = false)
    : IRequest<ResultadoPaginado<IncidenciaListaDto>>;

public record IncidenciaListaDto(
    Guid Id, Guid CentroId, string CentroNombre, Guid? TrabajadorId, string? TrabajadorNombre, TipoIncidencia Tipo,
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
        else if (request.Resuelta is not null)
            consulta = consulta.Where(x => x.incidencia.Resuelta == request.Resuelta);

        if (!string.IsNullOrWhiteSpace(request.Busqueda))
        {
            var busqueda = request.Busqueda.ToUpper();
            consulta = consulta.Where(x =>
                x.centro.Nombre.ToUpper().Contains(busqueda) ||
                x.incidencia.Descripcion.ToUpper().Contains(busqueda) ||
                (x.trabajador != null && (x.trabajador.Nombre + " " + x.trabajador.Apellidos).ToUpper().Contains(busqueda)));
        }

        var total = await consulta.CountAsync(cancellationToken);

        // Lista blanca de columnas ordenables — ver ObtenerClientesQuery.
        var ordenada = (request.OrdenarPor, request.Descendente) switch
        {
            (nameof(IncidenciaListaDto.CentroNombre), false) => consulta.OrderBy(x => x.centro.Nombre),
            (nameof(IncidenciaListaDto.CentroNombre), true) => consulta.OrderByDescending(x => x.centro.Nombre),
            (nameof(IncidenciaListaDto.TrabajadorNombre), false) => consulta.OrderBy(x => x.trabajador.Apellidos).ThenBy(x => x.trabajador.Nombre),
            (nameof(IncidenciaListaDto.TrabajadorNombre), true) => consulta.OrderByDescending(x => x.trabajador.Apellidos).ThenByDescending(x => x.trabajador.Nombre),
            (nameof(IncidenciaListaDto.Tipo), false) => consulta.OrderBy(x => x.incidencia.Tipo).ThenByDescending(x => x.incidencia.FechaOcurrencia),
            (nameof(IncidenciaListaDto.Tipo), true) => consulta.OrderByDescending(x => x.incidencia.Tipo).ThenByDescending(x => x.incidencia.FechaOcurrencia),
            // El enum de gravedad va de menor a mayor, así que descendente deja
            // arriba lo más grave — que es lo que se busca al ordenar por ella.
            (nameof(IncidenciaListaDto.Gravedad), false) => consulta.OrderBy(x => x.incidencia.Gravedad).ThenByDescending(x => x.incidencia.FechaOcurrencia),
            (nameof(IncidenciaListaDto.Gravedad), true) => consulta.OrderByDescending(x => x.incidencia.Gravedad).ThenByDescending(x => x.incidencia.FechaOcurrencia),
            (nameof(IncidenciaListaDto.FechaOcurrencia), false) => consulta.OrderBy(x => x.incidencia.FechaOcurrencia),
            (nameof(IncidenciaListaDto.Resuelta), false) => consulta.OrderBy(x => x.incidencia.Resuelta).ThenByDescending(x => x.incidencia.FechaOcurrencia),
            (nameof(IncidenciaListaDto.Resuelta), true) => consulta.OrderByDescending(x => x.incidencia.Resuelta).ThenByDescending(x => x.incidencia.FechaOcurrencia),
            _ => consulta.OrderByDescending(x => x.incidencia.FechaOcurrencia)
        };
        // Desempate estable: sin un criterio total, PostgreSQL puede devolver
        // las filas empatadas en distinto orden entre una página y otra, y al
        // paginar en SQL eso hace que una fila aparezca dos veces o no
        // aparezca nunca. El Id no se ordena nunca por sí solo — solo cierra
        // el orden que haya elegido el usuario.
        ordenada = ordenada.ThenBy(x => x.incidencia.Id);

        var elementos = await ordenada
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(x => new IncidenciaListaDto(
                x.incidencia.Id,
                x.centro.Id,
                x.centro.Nombre,
                x.trabajador == null ? null : (Guid?)x.trabajador.Id,
                x.trabajador == null ? null : x.trabajador.Nombre + " " + x.trabajador.Apellidos,
                x.incidencia.Tipo,
                x.incidencia.Gravedad,
                x.incidencia.FechaOcurrencia,
                x.incidencia.Resuelta))
            .ToListAsync(cancellationToken);

        return new ResultadoPaginado<IncidenciaListaDto>(elementos, total, request.Pagina, request.TamanoPagina);
    }
}
