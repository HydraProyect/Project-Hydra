using CaeManager.Application.Common;
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
    IPlantillasQueryContext plantillasContext, ITrabajadoresQueryContext trabajadoresContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerLoteGeneracionDocumentosQuery, Result<LoteGeneracionDocumentoDto>>
{
    public async Task<Result<LoteGeneracionDocumentoDto>> Handle(
        ObtenerLoteGeneracionDocumentosQuery request, CancellationToken cancellationToken)
    {
        // Proyecta a propósito: Soporte TALVEG no tiene ContextoJson, y materializar la entidad
        // daría 42501 en Sesión Privilegiada.
        var lote = await plantillasContext.LotesGeneracionDocumento
            .Where(l => l.Id == request.LoteGeneracionDocumentoId)
            .Select(l => new { l.Id, l.PlantillaDocumentoVersionId, l.Estado, l.TotalItems, l.ItemsCompletados, l.ItemsFallidos })
            .FirstOrDefaultAsync(cancellationToken);
        if (lote is null)
            return LoteNoEncontrado();

        var items = await plantillasContext.ItemsGeneracionDocumento
            .Where(i => i.LoteGeneracionDocumentoId == lote.Id)
            .ToListAsync(cancellationToken);

        // Asignación de Cartera por Id (OWASP ASVS 4.0.3 V4.2.1): el lote solo lo lee quien podría haberlo iniciado, con
        // el criterio de IniciarLoteGeneracionDocumentosCommandHandler: plantilla dentro de alcance y todos sus
        // Trabajadores visibles. Si no, responde igual que un lote que no existe, para no delatarlo.
        if (!await LoteDentroDeAlcanceAsync(lote.PlantillaDocumentoVersionId, items.Select(i => i.TrabajadorId), cancellationToken))
            return LoteNoEncontrado();

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

    private async Task<bool> LoteDentroDeAlcanceAsync(
        Guid plantillaDocumentoVersionId, IEnumerable<Guid> trabajadorIds, CancellationToken cancellationToken)
    {
        if (await alcanceDatos.TieneAccesoTotalAsync(cancellationToken))
            return true;

        var plantillaId = await plantillasContext.PlantillasDocumentoVersion
            .Where(v => v.Id == plantillaDocumentoVersionId)
            .Select(v => (Guid?)v.PlantillaDocumentoId)
            .FirstOrDefaultAsync(cancellationToken);
        var plantilla = plantillaId is { } id
            ? await plantillasContext.PlantillasDocumento.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            : null;
        // Una plantilla que ya no se lee no da alcance: falla cerrado.
        if (plantilla is null || !await alcanceDatos.PlantillaDentroDeAlcanceAsync(plantilla, cancellationToken))
            return false;

        var trabajadoresVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        return trabajadoresVisibles is null || trabajadorIds.All(trabajadoresVisibles.ToHashSet().Contains);
    }

    private static Result<LoteGeneracionDocumentoDto> LoteNoEncontrado() =>
        Result.Fallo<LoteGeneracionDocumentoDto>(Error.Crear("Plantilla.LoteNoEncontrado", "No encontramos este lote de generación."));
}
