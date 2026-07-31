using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Evaluaciones.Queries.ObtenerEvaluacionPorId;

public record ObtenerEvaluacionPorIdQuery(Guid Id) : IRequest<EvaluacionDetalleDto?>;

public record EvaluacionDetalleDto(
    Guid Id, Guid CentroId, string CentroNombre, Guid? TrabajadorId, DateOnly Fecha, int Puntuacion,
    string? Observaciones, Guid Version);

public class ObtenerEvaluacionPorIdQueryHandler(IApplicationDbContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerEvaluacionPorIdQuery, EvaluacionDetalleDto?>
{
    public async Task<EvaluacionDetalleDto?> Handle(ObtenerEvaluacionPorIdQuery request, CancellationToken cancellationToken)
    {
        var evaluacion = await (
            from e in dbContext.Evaluaciones
            join centro in dbContext.Centros on e.CentroId equals centro.Id
            where e.Id == request.Id
            select new EvaluacionDetalleDto(
                e.Id, e.CentroId, centro.Nombre, e.TrabajadorId, e.Fecha, e.Puntuacion, e.Observaciones, e.Version))
            .FirstOrDefaultAsync(cancellationToken);

        if (evaluacion is null) return null;
        if (!await alcanceDatos.CentroVisibleAsync(evaluacion.CentroId, cancellationToken)) return null;

        return evaluacion;
    }
}
