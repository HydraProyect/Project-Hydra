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

        var items = await plantillasContext.ItemsGeneracionDocumento
            .Where(i => i.LoteGeneracionDocumentoId == lote.Id)
            .ToListAsync(cancellationToken);

        // Asignación de Cartera por Id (OWASP ASVS 4.0.3 V4.2.1): el lote solo lo lee quien podría haberlo iniciado, con
        // el criterio de IniciarLoteGeneracionDocumentosCommandHandler: plantilla dentro de alcance y todos sus
        // Trabajadores visibles. Si no, responde igual que un lote que no existe, para no delatarlo.
        if (!await alcanceDatos.LoteDentroDeAlcanceAsync(
                lote.PlantillaDocumentoVersionId, items.Select(i => i.TrabajadorId).Distinct().ToList(), plantillasContext, cancellationToken))
            return LoteNoEncontrado();

        // Pasar la puerta del lote no basta para ver lo que generó: un ítem con documento generado solo se enseña si ese
        // registro pasa la misma puerta que la lista de trazabilidad (Centro, Empresa, plantilla y Documento legible), con
        // el mismo criterio fila a fila. Un Trabajador visible por estructura propia puede estar en un Centro fuera de la
        // Cartera: sin esto el progreso devolvía la traza que la lista oculta. Un ítem pendiente o fallido no generó
        // nada, y lo decide solo la puerta del lote. Los recuentos salen de los ítems que quedan, no de las columnas del
        // lote, para que el progreso no cuente lo que no enseña.
        items = await ItemsVisiblesAsync(items, cancellationToken);

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
            lote.Id, lote.Estado,
            TotalItems: items.Count,
            ItemsCompletados: items.Count(i => i.Estado is EstadoItemGeneracion.Completado or EstadoItemGeneracion.CompletadoConAvisos),
            ItemsFallidos: items.Count(i => i.Estado == EstadoItemGeneracion.Fallido),
            itemsDto));
    }

    private async Task<List<ItemGeneracionDocumento>> ItemsVisiblesAsync(
        List<ItemGeneracionDocumento> items, CancellationToken cancellationToken)
    {
        if (await alcanceDatos.TieneAccesoTotalAsync(cancellationToken))
            return items;

        var idsGenerados = items.Where(i => i.DocumentoGeneradoId is not null).Select(i => i.DocumentoGeneradoId!.Value).Distinct().ToList();
        if (idsGenerados.Count == 0)
            return items;

        // Proyecta a propósito, como la lista: sin CodigoSeguroVerificacion ni DatosUtilizadosJson.
        var generados = await plantillasContext.DocumentosGenerados
            .Where(d => idsGenerados.Contains(d.Id))
            .Select(d => new CoordenadasDocumentoGenerado(
                d.Id, d.DocumentoId, d.PlantillaDocumentoVersionId, d.TrabajadorId, d.EmpresaId, d.CentroId))
            .ToListAsync(cancellationToken);
        var visibles = await alcanceDatos.DocumentosGeneradosVisiblesAsync(
            generados, plantillasContext, documentosContext, proyectosContext, cancellationToken);

        // Un ítem que apunta a un registro que ya no se lee (inexistente u oculto) falla cerrado: no se enseña.
        return items.Where(i => i.DocumentoGeneradoId is not { } generadoId || visibles.Contains(generadoId)).ToList();
    }

    private static Result<LoteGeneracionDocumentoDto> LoteNoEncontrado() =>
        Result.Fallo<LoteGeneracionDocumentoDto>(Error.Crear("Plantilla.LoteNoEncontrado", "No encontramos este lote de generación."));
}
