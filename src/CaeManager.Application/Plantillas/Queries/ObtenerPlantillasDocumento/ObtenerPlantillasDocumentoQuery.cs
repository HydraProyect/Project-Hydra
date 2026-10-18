using CaeManager.Application.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Plantillas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Plantillas.Queries.ObtenerPlantillasDocumento;

/// <summary>Lista de plantillas <see cref="OrigenPlantilla.Externa"/> del tenant — las TalvegPropio no se gestionan desde esta UI (§ 2.2, catálogo sembrado por código).</summary>
public record ObtenerPlantillasDocumentoQuery : IRequest<IReadOnlyList<PlantillaDocumentoListaDto>>;

public record PlantillaDocumentoListaDto(
    Guid Id,
    string Nombre,
    string? Descripcion,
    AmbitoAplicacion AmbitoAplicacion,
    FormatoOrigenPlantilla FormatoOrigen,
    EstadoPlantillaDocumento Estado,
    Guid? VersionActualId,
    Guid UltimaVersionId,
    EstadoConfiguracionPlantilla UltimaVersionEstado);

/// <remarks>
/// Asignación de Cartera: solo se listan las plantillas que alcanza quien mira (<see cref="AlcanceDePlantillas"/>), el
/// mismo criterio con el que se crean y se modifican. Una plantilla atada a un Centro o a un Cliente empresarial de otra
/// cartera no aparece; las generales del Tenant, sí.
/// </remarks>
public class ObtenerPlantillasDocumentoQueryHandler(IPlantillasQueryContext plantillasContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerPlantillasDocumentoQuery, IReadOnlyList<PlantillaDocumentoListaDto>>
{
    public async Task<IReadOnlyList<PlantillaDocumentoListaDto>> Handle(
        ObtenerPlantillasDocumentoQuery request, CancellationToken cancellationToken)
    {
        var delTenant = await plantillasContext.PlantillasDocumento
            .Where(d => d.Origen == OrigenPlantilla.Externa)
            .ToListAsync(cancellationToken);

        var documentos = new List<PlantillaDocumento>(delTenant.Count);
        foreach (var documento in delTenant)
        {
            if (await alcanceDatos.PlantillaDentroDeAlcanceAsync(documento, cancellationToken))
                documentos.Add(documento);
        }

        if (documentos.Count == 0)
            return [];

        var idsDocumentos = documentos.Select(d => d.Id).ToList();
        var versiones = await plantillasContext.PlantillasDocumentoVersion
            .Where(v => idsDocumentos.Contains(v.PlantillaDocumentoId))
            .ToListAsync(cancellationToken);

        var ultimaVersionPorDocumento = versiones
            .GroupBy(v => v.PlantillaDocumentoId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.NumeroVersion).First());

        return documentos
            .Where(d => ultimaVersionPorDocumento.ContainsKey(d.Id))
            .Select(d =>
            {
                var ultimaVersion = ultimaVersionPorDocumento[d.Id];
                return new PlantillaDocumentoListaDto(
                    d.Id, d.Nombre, d.Descripcion, d.AmbitoAplicacion, d.FormatoOrigen, d.Estado,
                    d.VersionActualId, ultimaVersion.Id, ultimaVersion.EstadoConfiguracion);
            })
            .OrderBy(d => d.Nombre)
            .ToList();
    }
}
