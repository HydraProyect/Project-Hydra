using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Plantillas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Plantillas.Queries.ObtenerPlantillaDocumentoVersion;

/// <summary>Todo lo que necesita el editor visual (ADR-010 § 2.3) en una sola carga: metadata de la versión y de su plantilla, más los elementos ya guardados.</summary>
public record ObtenerPlantillaDocumentoVersionQuery(Guid PlantillaDocumentoVersionId)
    : IRequest<Result<PlantillaDocumentoVersionDetalleDto>>;

public record PlantillaDocumentoVersionDetalleDto(
    Guid PlantillaDocumentoVersionId,
    Guid PlantillaDocumentoId,
    string NombrePlantilla,
    AmbitoAplicacion AmbitoAplicacion,
    FormatoOrigenPlantilla FormatoOrigen,
    EstadoConfiguracionPlantilla EstadoConfiguracion,
    string ArchivoOriginalUrl,
    IReadOnlyList<PlantillaElementoDetalleDto> Elementos);

public record PlantillaElementoDetalleDto(
    Guid Id,
    TipoElementoPlantilla Tipo,
    int Pagina,
    double X,
    double Y,
    double Ancho,
    double Alto,
    string EtiquetaVisible,
    FuenteDatoPlantilla? FuenteDato,
    string? ValorConstante,
    string? Formato,
    bool Obligatorio,
    RolFirmantePlantilla? RolFirmante,
    string? NombreCampoAcroForm);

/// <remarks>
/// Asignación de Cartera: una versión de una plantilla fuera del alcance de quien mira (<see cref="AlcanceDePlantillas"/>)
/// responde igual que una versión que no existe, para no delatarla.
/// </remarks>
public class ObtenerPlantillaDocumentoVersionQueryHandler(IPlantillasQueryContext plantillasContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerPlantillaDocumentoVersionQuery, Result<PlantillaDocumentoVersionDetalleDto>>
{
    private static Result<PlantillaDocumentoVersionDetalleDto> VersionNoEncontrada() =>
        Result.Fallo<PlantillaDocumentoVersionDetalleDto>(
            Error.Crear("Plantilla.VersionNoEncontrada", "No encontramos esta versión de plantilla."));

    public async Task<Result<PlantillaDocumentoVersionDetalleDto>> Handle(
        ObtenerPlantillaDocumentoVersionQuery request, CancellationToken cancellationToken)
    {
        var version = await plantillasContext.PlantillasDocumentoVersion
            .FirstOrDefaultAsync(v => v.Id == request.PlantillaDocumentoVersionId, cancellationToken);
        if (version is null)
            return VersionNoEncontrada();

        var documento = await plantillasContext.PlantillasDocumento
            .FirstOrDefaultAsync(d => d.Id == version.PlantillaDocumentoId, cancellationToken);
        if (documento is null)
            return Result.Fallo<PlantillaDocumentoVersionDetalleDto>(
                Error.Crear("Plantilla.NoEncontrada", "No encontramos la plantilla de esta versión."));

        if (!await alcanceDatos.PlantillaDentroDeAlcanceAsync(documento, cancellationToken))
            return VersionNoEncontrada();

        var elementos = await plantillasContext.PlantillasElemento
            .Where(e => e.PlantillaDocumentoVersionId == version.Id)
            .ToListAsync(cancellationToken);

        var elementosDto = elementos
            .Select(e => new PlantillaElementoDetalleDto(
                e.Id, e.Tipo, e.Pagina, e.X, e.Y, e.Ancho, e.Alto, e.EtiquetaVisible,
                e.FuenteDato, e.ValorConstante, e.Formato, e.Obligatorio, e.RolFirmante, e.NombreCampoAcroForm))
            .ToList();

        return Result.Exito(new PlantillaDocumentoVersionDetalleDto(
            version.Id, documento.Id, documento.Nombre, documento.AmbitoAplicacion, documento.FormatoOrigen, version.EstadoConfiguracion,
            version.ArchivoOriginalUrl ?? string.Empty, elementosDto));
    }
}
