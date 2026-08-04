using CaeManager.Application.Common;
using CaeManager.Application.Centros;
using CaeManager.Application.Evaluaciones;
using CaeManager.Application.Trabajadores;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Evaluaciones.Queries.ObtenerEvaluaciones;

public record ObtenerEvaluacionesQuery(
    string? Busqueda, int Pagina = 1, int TamanoPagina = 20,
    string? OrdenarPor = null, bool Descendente = false)
    : IRequest<ResultadoPaginado<EvaluacionListaDto>>;

public record EvaluacionListaDto(
    Guid Id, Guid CentroId, string CentroNombre, Guid? TrabajadorId, string? TrabajadorNombre, DateOnly Fecha, int Puntuacion);

public class ObtenerEvaluacionesQueryHandler(ICentrosQueryContext centrosContext, IEvaluacionesQueryContext evaluacionesContext, ITrabajadoresQueryContext trabajadoresContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerEvaluacionesQuery, ResultadoPaginado<EvaluacionListaDto>>
{
    public async Task<ResultadoPaginado<EvaluacionListaDto>> Handle(
        ObtenerEvaluacionesQuery request, CancellationToken cancellationToken)
    {
        var consulta =
            from evaluacion in evaluacionesContext.Evaluaciones
            join centro in centrosContext.Centros on evaluacion.CentroId equals centro.Id
            join trabajador in trabajadoresContext.Trabajadores on evaluacion.TrabajadorId equals trabajador.Id into trabajadores
            from trabajador in trabajadores.DefaultIfEmpty()
            select new { evaluacion, centro, trabajador };

        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        if (centroIdsVisibles is not null)
            consulta = consulta.Where(x => centroIdsVisibles.Contains(x.centro.Id));

        if (!string.IsNullOrWhiteSpace(request.Busqueda))
        {
            var busqueda = request.Busqueda.ToUpper();
            consulta = consulta.Where(x =>
                x.centro.Nombre.ToUpper().Contains(busqueda) ||
                (x.trabajador != null && (x.trabajador.Nombre + " " + x.trabajador.Apellidos).ToUpper().Contains(busqueda)));
        }

        var total = await consulta.CountAsync(cancellationToken);

        // Lista blanca de columnas ordenables — ver ObtenerClientesQuery.
        var ordenada = (request.OrdenarPor, request.Descendente) switch
        {
            (nameof(EvaluacionListaDto.CentroNombre), false) => consulta.OrderBy(x => x.centro.Nombre),
            (nameof(EvaluacionListaDto.CentroNombre), true) => consulta.OrderByDescending(x => x.centro.Nombre),
            (nameof(EvaluacionListaDto.TrabajadorNombre), false) => consulta.OrderBy(x => x.trabajador.Apellidos).ThenBy(x => x.trabajador.Nombre),
            (nameof(EvaluacionListaDto.TrabajadorNombre), true) => consulta.OrderByDescending(x => x.trabajador.Apellidos).ThenByDescending(x => x.trabajador.Nombre),
            (nameof(EvaluacionListaDto.Fecha), false) => consulta.OrderBy(x => x.evaluacion.Fecha),
            (nameof(EvaluacionListaDto.Puntuacion), false) => consulta.OrderBy(x => x.evaluacion.Puntuacion).ThenByDescending(x => x.evaluacion.Fecha),
            (nameof(EvaluacionListaDto.Puntuacion), true) => consulta.OrderByDescending(x => x.evaluacion.Puntuacion).ThenByDescending(x => x.evaluacion.Fecha),
            _ => consulta.OrderByDescending(x => x.evaluacion.Fecha)
        };
        // Desempate estable: sin un criterio total, PostgreSQL puede devolver
        // las filas empatadas en distinto orden entre una página y otra, y al
        // paginar en SQL eso hace que una fila aparezca dos veces o no
        // aparezca nunca. El Id no se ordena nunca por sí solo — solo cierra
        // el orden que haya elegido el usuario.
        ordenada = ordenada.ThenBy(x => x.evaluacion.Id);

        var elementos = await ordenada
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(x => new EvaluacionListaDto(
                x.evaluacion.Id,
                x.centro.Id,
                x.centro.Nombre,
                x.trabajador == null ? null : (Guid?)x.trabajador.Id,
                x.trabajador == null ? null : x.trabajador.Nombre + " " + x.trabajador.Apellidos,
                x.evaluacion.Fecha,
                x.evaluacion.Puntuacion))
            .ToListAsync(cancellationToken);

        return new ResultadoPaginado<EvaluacionListaDto>(elementos, total, request.Pagina, request.TamanoPagina);
    }
}
