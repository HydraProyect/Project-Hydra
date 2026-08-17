using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Importacion.Queries.ObtenerHistorialImportaciones;

/// <summary>Últimas importaciones ejecutadas, para el sidebar del wizard (Configuración → Importar datos).</summary>
public record ObtenerHistorialImportacionesQuery(int Limite = 10) : IRequest<IReadOnlyList<HistorialImportacionDto>>;

public record HistorialImportacionDto(
    Guid Id, string Plantilla, string NombreArchivo, DateTime EjecutadaEnUtc, Guid EjecutadaPorUsuarioId,
    bool Exitosa, int TotalCreados, int TotalAdvertencias, int TotalOmitidos, string? MensajeError);

public class ObtenerHistorialImportacionesQueryHandler(IImportacionQueryContext importacionContext)
    : IRequestHandler<ObtenerHistorialImportacionesQuery, IReadOnlyList<HistorialImportacionDto>>
{
    public async Task<IReadOnlyList<HistorialImportacionDto>> Handle(
        ObtenerHistorialImportacionesQuery request, CancellationToken cancellationToken) =>
        await importacionContext.HistorialImportaciones
            .OrderByDescending(h => h.EjecutadaEnUtc)
            .Take(request.Limite)
            .Select(h => new HistorialImportacionDto(
                h.Id, h.Plantilla, h.NombreArchivo, h.EjecutadaEnUtc, h.EjecutadaPorUsuarioId,
                h.Exitosa, h.TotalCreados, h.TotalAdvertencias, h.TotalOmitidos, h.MensajeError))
            .ToListAsync(cancellationToken);
}
