using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Evaluaciones.Queries.ObtenerEvaluaciones;

public record ObtenerEvaluacionesQuery(string? Busqueda, int Pagina = 1, int TamanoPagina = 20)
    : IRequest<ResultadoPaginado<EvaluacionListaDto>>;

public record EvaluacionListaDto(
    Guid Id, string CentroNombre, string? TrabajadorNombre, DateOnly Fecha, int Puntuacion);

public class ObtenerEvaluacionesQueryHandler(IApplicationDbContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerEvaluacionesQuery, ResultadoPaginado<EvaluacionListaDto>>
{
    public async Task<ResultadoPaginado<EvaluacionListaDto>> Handle(
        ObtenerEvaluacionesQuery request, CancellationToken cancellationToken)
    {
        var consulta =
            from evaluacion in dbContext.Evaluaciones
            join centro in dbContext.Centros on evaluacion.CentroId equals centro.Id
            join trabajador in dbContext.Trabajadores on evaluacion.TrabajadorId equals trabajador.Id into trabajadores
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

        var elementos = await consulta
            .OrderByDescending(x => x.evaluacion.Fecha)
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(x => new EvaluacionListaDto(
                x.evaluacion.Id,
                x.centro.Nombre,
                x.trabajador == null ? null : x.trabajador.Nombre + " " + x.trabajador.Apellidos,
                x.evaluacion.Fecha,
                x.evaluacion.Puntuacion))
            .ToListAsync(cancellationToken);

        return new ResultadoPaginado<EvaluacionListaDto>(elementos, total, request.Pagina, request.TamanoPagina);
    }
}
