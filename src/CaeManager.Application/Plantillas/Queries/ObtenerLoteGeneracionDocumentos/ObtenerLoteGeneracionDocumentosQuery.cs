using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plantillas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Plantillas.Queries.ObtenerLoteGeneracionDocumentos;

/// <summary>Estado del lote y de cada uno de sus items — la UI la vuelve a llamar tras procesar cada <c>ItemGeneracionDocumento</c> para refrescar el progreso en pantalla.</summary>
public record ObtenerLoteGeneracionDocumentosQuery(Guid LoteGeneracionDocumentoId)
    : IRequest<Result<LoteGeneracionDocumentoDto>>;

public record LoteGeneracionDocumentoDto(
    Guid LoteId,
    EstadoLoteGeneracion Estado,
    int TotalItems,
    int ItemsCompletados,
    int ItemsFallidos,
    IReadOnlyList<ItemGeneracionDocumentoDto> Items);

public record ItemGeneracionDocumentoDto(
    Guid ItemId, Guid TrabajadorId, string TrabajadorNombreCompleto, EstadoItemGeneracion Estado, string? Error, Guid? DocumentoGeneradoId);

public class ObtenerLoteGeneracionDocumentosQueryHandler(
    IPlantillasQueryContext plantillasContext, ITrabajadoresQueryContext trabajadoresContext)
    : IRequestHandler<ObtenerLoteGeneracionDocumentosQuery, Result<LoteGeneracionDocumentoDto>>
{
    public async Task<Result<LoteGeneracionDocumentoDto>> Handle(
        ObtenerLoteGeneracionDocumentosQuery request, CancellationToken cancellationToken)
    {
        var lote = await plantillasContext.LotesGeneracionDocumento
            .FirstOrDefaultAsync(l => l.Id == request.LoteGeneracionDocumentoId, cancellationToken);
        if (lote is null)
            return Result.Fallo<LoteGeneracionDocumentoDto>(Error.Crear("Plantilla.LoteNoEncontrado", "No encontramos este lote de generación."));

        var items = await plantillasContext.ItemsGeneracionDocumento
            .Where(i => i.LoteGeneracionDocumentoId == lote.Id)
            .ToListAsync(cancellationToken);

        var idsTrabajadores = items.Select(i => i.TrabajadorId).Distinct().ToList();
        var trabajadores = await trabajadoresContext.Trabajadores
            .Where(t => idsTrabajadores.Contains(t.Id))
            .Select(t => new { t.Id, t.NombreCompleto })
            .ToListAsync(cancellationToken);
        var nombrePorTrabajadorId = trabajadores.ToDictionary(t => t.Id, t => t.NombreCompleto);

        var itemsDto = items
            .Select(i => new ItemGeneracionDocumentoDto(
                i.Id, i.TrabajadorId, nombrePorTrabajadorId.GetValueOrDefault(i.TrabajadorId, "—"),
                i.Estado, i.Error, i.DocumentoGeneradoId))
            .ToList();

        return Result.Exito(new LoteGeneracionDocumentoDto(
            lote.Id, lote.Estado, lote.TotalItems, lote.ItemsCompletados, lote.ItemsFallidos, itemsDto));
    }
}
