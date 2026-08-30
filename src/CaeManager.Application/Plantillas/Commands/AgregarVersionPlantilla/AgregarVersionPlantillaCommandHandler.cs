using System.Security.Cryptography;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plantillas;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Plantillas.Commands.AgregarVersionPlantilla;

public class AgregarVersionPlantillaCommandHandler(
    IPlantillaDocumentoRepository plantillaRepositorio,
    IPlantillaDocumentoVersionRepository versionRepositorio,
    IFileStorageService almacenamientoArchivos,
    IUnitOfWork unitOfWork,
    ILogger<AgregarVersionPlantillaCommandHandler> logger)
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

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException)
        {
            // Auditoría de seguridad del módulo (2026-08-30), pendiente 3.5:
            // dos altas de versión concurrentes sobre la MISMA plantilla
            // pueden calcular el mismo siguienteNumeroVersion (MAX+1 leído
            // antes de guardar) — la restricción única de BD
            // (TenantId, PlantillaDocumentoId, NumeroVersion) impide que las
            // dos lleguen a persistirse, pero antes esa excepción llegaba sin
            // traducir al llamador y el blob ya subido quedaba huérfano. No
            // es DbUpdateConcurrencyException (ningún token optimista chocó,
            // son dos inserts nuevos) así que ConcurrenciaBehavior no la ve.
            await EliminarBlobHuerfanoAsync(archivoUrl, cancellationToken);
            return Result.Fallo<AgregarVersionPlantillaResultadoDto>(Error.Crear(
                "Plantilla.VersionEnConflicto",
                "Otra persona guardó una versión de esta plantilla al mismo tiempo. Vuelve a intentarlo."));
        }

        return Result.Exito(new AgregarVersionPlantillaResultadoDto(nuevaVersion.Id, archivoIdentico));
    }

    private async Task EliminarBlobHuerfanoAsync(string archivoUrl, CancellationToken cancellationToken)
    {
        try
        {
            await almacenamientoArchivos.EliminarAsync(archivoUrl, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No se propaga: el fallo real ya es el conflicto de versión —
            // perder ese Result.Fallo por un problema al limpiar dejaría al
            // llamador sin explicación. Mismo patrón de compensación
            // best-effort que CompensacionBlobsHuerfanosIngesta (módulo 6):
            // si esta limpieza también falla, el blob queda huérfano sin más
            // reintento — no existe hoy un barrido periódico que lo recoja.
            logger.LogWarning(ex,
                "No se pudo eliminar el blob huérfano {ArchivoUrl} tras un conflicto de versión de plantilla.", archivoUrl);
        }
    }

    private static string CalcularHash(byte[] contenido) => Convert.ToHexStringLower(SHA256.HashData(contenido));
}
