using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.TiposDocumento.Queries.ObtenerTipoDocumentoPorId;

public record ObtenerTipoDocumentoPorIdQuery(Guid Id) : IRequest<TipoDocumentoDetalleDto?>;

public record TipoDocumentoDetalleDto(
    Guid Id,
    string Nombre,
    int? VigenciaMeses,
    bool AplicaVencimientoAutomatico,
    int Orden,
    string? Notas,
    string? Descripcion,
    string? CriteriosValidacion,
    string? SeSolicitaA,
    string? Observaciones,
    IReadOnlyList<Guid> CentroIds);

public class ObtenerTipoDocumentoPorIdQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<ObtenerTipoDocumentoPorIdQuery, TipoDocumentoDetalleDto?>
{
    public async Task<TipoDocumentoDetalleDto?> Handle(ObtenerTipoDocumentoPorIdQuery request, CancellationToken cancellationToken)
    {
        var tipoDocumento = await dbContext.TiposDocumento
            .Where(t => t.Id == request.Id)
            .Select(t => new
            {
                t.Id, t.Nombre, t.VigenciaMeses, t.AplicaVencimientoAutomatico, t.Orden, t.Notas,
                t.Descripcion, t.CriteriosValidacion, t.SeSolicitaA, t.Observaciones
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (tipoDocumento is null) return null;

        var centroIds = await dbContext.TiposDocumentoCentros
            .Where(tc => tc.TipoDocumentoId == request.Id)
            .Select(tc => tc.CentroId)
            .ToListAsync(cancellationToken);

        return new TipoDocumentoDetalleDto(
            tipoDocumento.Id, tipoDocumento.Nombre, tipoDocumento.VigenciaMeses, tipoDocumento.AplicaVencimientoAutomatico,
            tipoDocumento.Orden, tipoDocumento.Notas, tipoDocumento.Descripcion, tipoDocumento.CriteriosValidacion,
            tipoDocumento.SeSolicitaA, tipoDocumento.Observaciones, centroIds);
    }
}
