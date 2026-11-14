using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Proyectos;
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
    IPlantillasQueryContext plantillasContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IDocumentosQueryContext documentosContext,
    IProyectosQueryContext proyectosContext,
    IAlcanceDatosService alcanceDatos)
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

        // La instantánea de ítems que se devuelve se lee ANTES de la puerta, nunca después: un ítem solo avanza y un
        // DocumentoGeneradoId asignado no se retira, así que la puerta, que mira el estado actual, ve todo generado de esta
        // instantánea (y lo que se completó después). Leerla tras la puerta dejaba una ventana en la que un ítem completado
        // entre las dos lecturas salía en la respuesta con un generado sin comprobar (TOCTOU, Codex ronda 3, PR #2222).
        var items = await plantillasContext.ItemsGeneracionDocumento
            .Where(i => i.LoteGeneracionDocumentoId == lote.Id)
            .ToListAsync(cancellationToken);

        // Asignación de Cartera por Id (OWASP ASVS 4.0.3 V4.2.1), todo o nada (AlcanceDeLotesGeneracion.LoteVisibleEnteroAsync,
        // el mismo criterio que ProcesarItemLoteGeneracionCommand): plantilla dentro de alcance, todos sus Trabajadores
        // visibles y cada documento generado por sus ítems, los de la instantánea incluidos, visible en la lista de
        // trazabilidad. Si algo no pasa, responde igual que un lote que no existe, sin estado ni recuentos que lo delaten;
        // si pasa, el lote se ve entero, con su estado y sus recuentos reales, y se devuelve solo esa instantánea.
        var instantanea = items.Select(i => new ItemDeLoteLeido(i.TrabajadorId, i.DocumentoGeneradoId)).ToList();
        if (!await alcanceDatos.LoteVisibleEnteroAsync(
                lote.Id, lote.PlantillaDocumentoVersionId, instantanea,
                plantillasContext, documentosContext, proyectosContext, cancellationToken))
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

    private static Result<LoteGeneracionDocumentoDto> LoteNoEncontrado() =>
        Result.Fallo<LoteGeneracionDocumentoDto>(Error.Crear("Plantilla.LoteNoEncontrado", "No encontramos este lote de generación."));
}
