using CaeManager.Application.Common;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Documentos;
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
    AmbitoAplicacion AmbitoAplicacion,
    RequisitoDocumental Requerido,
    NaturalezaJuridica Naturaleza,
    string? Notas,
    string? Descripcion,
    string? CriteriosValidacion,
    string? SeSolicitaA,
    string? Observaciones,
    IReadOnlyList<Guid> CentroIds,
    IReadOnlyList<string> Aliases);

public class ObtenerTipoDocumentoPorIdQueryHandler(ITiposDocumentoQueryContext dbContext)
    : IRequestHandler<ObtenerTipoDocumentoPorIdQuery, TipoDocumentoDetalleDto?>
{
    public async Task<TipoDocumentoDetalleDto?> Handle(ObtenerTipoDocumentoPorIdQuery request, CancellationToken cancellationToken)
    {
        var tipoDocumento = await dbContext.TiposDocumento
            .Where(t => t.Id == request.Id)
            .Select(t => new
            {
                t.Id,
                t.Nombre,
                t.VigenciaMeses,
                t.AplicaVencimientoAutomatico,
                t.Orden,
                t.AmbitoAplicacion,
                t.Requerido,
                t.Naturaleza,
                t.Notas,
                t.Descripcion,
                t.CriteriosValidacion,
                t.SeSolicitaA,
                t.Observaciones
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (tipoDocumento is null) return null;

        var centroIds = await dbContext.TiposDocumentoCentros
            .Where(tc => tc.TipoDocumentoId == request.Id && tc.Incluido)
            .Select(tc => tc.CentroId)
            .ToListAsync(cancellationToken);

        var aliases = await dbContext.TiposDocumentoAlias
            .Where(a => a.TipoDocumentoId == request.Id)
            .Select(a => a.Texto)
            .ToListAsync(cancellationToken);

        return new TipoDocumentoDetalleDto(
            tipoDocumento.Id, tipoDocumento.Nombre, tipoDocumento.VigenciaMeses, tipoDocumento.AplicaVencimientoAutomatico,
            tipoDocumento.Orden, tipoDocumento.AmbitoAplicacion, tipoDocumento.Requerido, tipoDocumento.Naturaleza, tipoDocumento.Notas, tipoDocumento.Descripcion,
            tipoDocumento.CriteriosValidacion, tipoDocumento.SeSolicitaA, tipoDocumento.Observaciones, centroIds, aliases);
    }
}
