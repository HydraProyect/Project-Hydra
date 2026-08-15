using System.Security.Cryptography;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plantillas;
using MediatR;

namespace CaeManager.Application.Plantillas.Commands.AgregarVersionPlantilla;

public class AgregarVersionPlantillaCommandHandler(
    IPlantillaDocumentoRepository plantillaRepositorio,
    IPlantillaDocumentoVersionRepository versionRepositorio,
    IFileStorageService almacenamientoArchivos,
    IUnitOfWork unitOfWork)
    : IRequestHandler<AgregarVersionPlantillaCommand, Result<AgregarVersionPlantillaResultadoDto>>
{
    public async Task<Result<AgregarVersionPlantillaResultadoDto>> Handle(
        AgregarVersionPlantillaCommand request, CancellationToken cancellationToken)
    {
        var plantilla = await plantillaRepositorio.ObtenerPorIdAsync(request.PlantillaDocumentoId, cancellationToken);
        if (plantilla is null)
            return Result.Fallo<AgregarVersionPlantillaResultadoDto>(Error.Crear("Plantilla.NoEncontrada", "No encontramos esta plantilla."));

        var todasLasVersiones = await versionRepositorio.ObtenerPorPlantillaAsync(plantilla.Id, cancellationToken);
        var siguienteNumeroVersion = (todasLasVersiones.Count == 0 ? 0 : todasLasVersiones.Max(v => v.NumeroVersion)) + 1;

        var versionActual = plantilla.VersionActualId is { } versionActualId
            ? todasLasVersiones.FirstOrDefault(v => v.Id == versionActualId)
            : null;

        var hash = CalcularHash(request.Contenido);
        var archivoIdentico = versionActual?.HashSha256ArchivoOriginal == hash;

        using var flujo = new MemoryStream(request.Contenido);
        var archivoUrl = await almacenamientoArchivos.GuardarAsync(flujo, request.NombreArchivo, cancellationToken);

        var nuevaVersion = new PlantillaDocumentoVersion(plantilla.Id, siguienteNumeroVersion, archivoUrl, hash);

        if (versionActual is not null && versionActual.Elementos.Count > 0)
        {
            var elementosCopiados = versionActual.Elementos.Select(e => new PlantillaElemento(
                nuevaVersion.Id, e.Tipo, e.Pagina, e.X, e.Y, e.Ancho, e.Alto, e.EtiquetaVisible,
                e.FuenteDato, e.ValorConstante, e.Formato, e.Obligatorio, e.RolFirmante, e.NombreCampoAcroForm));
            nuevaVersion.EstablecerElementos(elementosCopiados);
            nuevaVersion.MarcarPendienteRevision();
        }

        versionRepositorio.Agregar(nuevaVersion);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(new AgregarVersionPlantillaResultadoDto(nuevaVersion.Id, archivoIdentico));
    }

    private static string CalcularHash(byte[] contenido) => Convert.ToHexStringLower(SHA256.HashData(contenido));
}
