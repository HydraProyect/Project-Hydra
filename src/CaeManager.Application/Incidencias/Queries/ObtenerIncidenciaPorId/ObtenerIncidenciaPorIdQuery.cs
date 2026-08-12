using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Incidencias;
using CaeManager.Domain.Incidencias;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Incidencias.Queries.ObtenerIncidenciaPorId;

public record ObtenerIncidenciaPorIdQuery(Guid Id) : IRequest<IncidenciaDetalleDto?>;

public record IncidenciaDetalleDto(
    Guid Id, Guid CentroId, string CentroNombre, Guid? TrabajadorId, TipoIncidencia Tipo,
    GravedadIncidencia Gravedad, DateOnly FechaOcurrencia, string Descripcion, bool Resuelta, Guid Version);

public class ObtenerIncidenciaPorIdQueryHandler(ICentrosQueryContext centrosContext, IIncidenciasQueryContext incidenciasContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerIncidenciaPorIdQuery, IncidenciaDetalleDto?>
{
    public async Task<IncidenciaDetalleDto?> Handle(ObtenerIncidenciaPorIdQuery request, CancellationToken cancellationToken)
    {
        var incidencia = await (
            from i in incidenciasContext.Incidencias
            join centro in centrosContext.Centros on i.CentroId equals centro.Id
            where i.Id == request.Id
            select new IncidenciaDetalleDto(
                i.Id, i.CentroId, centro.Nombre, i.TrabajadorId, i.Tipo, i.Gravedad, i.FechaOcurrencia, i.Descripcion,
                i.Resuelta, i.Version))
            .FirstOrDefaultAsync(cancellationToken);

        if (incidencia is null) return null;
        if (!await alcanceDatos.CentroVisibleAsync(incidencia.CentroId, cancellationToken)) return null;

        return incidencia;
    }
}
