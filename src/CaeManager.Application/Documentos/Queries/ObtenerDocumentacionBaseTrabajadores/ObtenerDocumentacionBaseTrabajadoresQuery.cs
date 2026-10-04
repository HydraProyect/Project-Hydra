using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos.DocumentacionBase;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Queries.ObtenerDocumentacionBaseTrabajadores;

/// <summary>
/// Documentación base de varios Trabajadores a la vez (una página de lista, o uno solo para
/// el cajón y la ficha). Solo salen los Trabajadores visibles en el alcance del actor: uno
/// fuera de cartera no aparece en el resultado, igual que su Documento no es legible por Id.
/// </summary>
public record ObtenerDocumentacionBaseTrabajadoresQuery(IReadOnlyCollection<Guid> TrabajadorIds)
    : IRequest<IReadOnlyDictionary<Guid, DocumentacionBaseTrabajadorDto>>;

public class ObtenerDocumentacionBaseTrabajadoresQueryHandler(
    IConfiguracionQueryContext configuracionContext,
    IDocumentosQueryContext documentosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerDocumentacionBaseTrabajadoresQuery, IReadOnlyDictionary<Guid, DocumentacionBaseTrabajadorDto>>
{
    public const int MaximoTrabajadores = 200;

    public async Task<IReadOnlyDictionary<Guid, DocumentacionBaseTrabajadorDto>> Handle(
        ObtenerDocumentacionBaseTrabajadoresQuery request, CancellationToken cancellationToken)
    {
        var pedidos = request.TrabajadorIds.Distinct().Take(MaximoTrabajadores).ToList();
        if (pedidos.Count == 0)
            return new Dictionary<Guid, DocumentacionBaseTrabajadorDto>();

        var visibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        if (visibles is not null)
            pedidos = pedidos.Where(visibles.Contains).ToList();
        if (pedidos.Count == 0)
            return new Dictionary<Guid, DocumentacionBaseTrabajadorDto>();

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DiaDeNegocio.Hoy();

        // Solo los tipos básicos: el filtro por nombre se hace en memoria sobre el catálogo (pocas filas).
        var tiposBasicos = (await tiposDocumentoContext.TiposDocumento
                .Select(t => new { t.Id, t.Nombre })
                .ToListAsync(cancellationToken))
            .Where(t => DocumentacionBaseTrabajador.Clasificar(t.Nombre) is not null)
            .ToDictionary(t => t.Id, t => t.Nombre);

        var tipoIds = tiposBasicos.Keys.ToList();
        var filas = await documentosContext.Documentos.Operativos()
            .Where(d => d.TrabajadorId != null && pedidos.Contains(d.TrabajadorId.Value) && tipoIds.Contains(d.TipoDocumentoId))
            .Select(d => new { TrabajadorId = d.TrabajadorId!.Value, d.Id, d.TipoDocumentoId, d.EstadoVigencia, d.FechaVencimiento, d.FechaEmision, d.CreadoEnUtc })
            .ToListAsync(cancellationToken);

        var porTrabajador = filas.ToLookup(f => f.TrabajadorId);
        return pedidos.ToDictionary(
            id => id,
            id => DocumentacionBaseTrabajador.Calcular(
                porTrabajador[id].Select(f => new DocumentoParaDocumentacionBase(
                    f.Id, tiposBasicos[f.TipoDocumentoId], f.EstadoVigencia, f.FechaVencimiento, f.FechaEmision, f.CreadoEnUtc)),
                hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias));
    }
}
