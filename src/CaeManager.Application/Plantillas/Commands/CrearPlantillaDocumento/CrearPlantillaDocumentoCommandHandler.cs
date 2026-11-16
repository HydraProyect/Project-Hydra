using System.Security.Cryptography;
using CaeManager.Application.Common;
using CaeManager.Application.Common.Archivos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plantillas;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Plantillas.Commands.CrearPlantillaDocumento;

public class CrearPlantillaDocumentoCommandHandler(
    IPlantillaDocumentoRepository documentoRepositorio,
    IPlantillaDocumentoVersionRepository versionRepositorio,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IFileStorageService almacenamientoArchivos,
    IAlcanceDatosService alcanceDatos,
    IUnitOfWork unitOfWork,
    IArchivosPendientesDeReferencia archivosPendientes,
    ILogger<CrearPlantillaDocumentoCommandHandler> logger)
    : IRequestHandler<CrearPlantillaDocumentoCommand, Result<CrearPlantillaDocumentoResultadoDto>>
{
    public async Task<Result<CrearPlantillaDocumentoResultadoDto>> Handle(
        CrearPlantillaDocumentoCommand request, CancellationToken cancellationToken)
    {
        if (request.CentroId is { } centroId)
        {
            var centrosVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
            if (centrosVisibles is not null && !centrosVisibles.Contains(centroId))
                return Result.Fallo<CrearPlantillaDocumentoResultadoDto>(Error.Crear("Plantilla.CentroSinAcceso", "No encontramos este centro."));
        }

        if (request.ClienteId is { } clienteId)
        {
            var clientesVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);
            if (clientesVisibles is not null && !clientesVisibles.Contains(clienteId))
                return Result.Fallo<CrearPlantillaDocumentoResultadoDto>(Error.Crear("Plantilla.ClienteSinAcceso", "No encontramos este Cliente."));
        }

        var tipoDocumento = await tiposDocumentoContext.TiposDocumento
            .FirstOrDefaultAsync(t => t.Id == request.TipoDocumentoId, cancellationToken);
        if (tipoDocumento is null)
            return Result.Fallo<CrearPlantillaDocumentoResultadoDto>(Error.Crear("Plantilla.TipoDocumentoNoEncontrado", "No encontramos este tipo de documento."));
        if (tipoDocumento.AmbitoAplicacion != request.AmbitoAplicacion)
            return Result.Fallo<CrearPlantillaDocumentoResultadoDto>(Error.Crear(
                "Plantilla.AmbitoIncorrecto",
                $"\"{tipoDocumento.Nombre}\" es un tipo de documento de otro ámbito — no coincide con el ámbito elegido para esta plantilla."));

        if (!ValidadorFirmaArchivo.EsDocumentoValido(request.Contenido, request.NombreArchivo)
            || !string.Equals(Path.GetExtension(request.NombreArchivo), ".pdf", StringComparison.OrdinalIgnoreCase))
            return Result.Fallo<CrearPlantillaDocumentoResultadoDto>(Error.Crear("Plantilla.ArchivoNoValido", ValidadorFirmaArchivo.MensajePlantillaNoValida));

        var hash = CalcularHash(request.Contenido);

        using var flujo = new MemoryStream(request.Contenido);
        var archivoUrl = await almacenamientoArchivos.GuardarAsync(flujo, "plantilla.pdf", cancellationToken);
        // Hasta confirmar la plantilla y su primera versión, una excepción dejaría el PDF sin versión que lo referencie:
        // queda apuntado en el outbox del Tenant propietario y el barrido lo recoge a las 24 h (issue #2123).
        await archivosPendientes.ApuntarOEliminarAsync(almacenamientoArchivos, archivoUrl, logger, cancellationToken);

        var documento = new PlantillaDocumento(
            OrigenPlantilla.Externa, request.Nombre, request.AmbitoAplicacion, request.FormatoOrigen, request.TipoDocumentoId,
            request.Descripcion, request.CentroId, request.ClienteId);
        documentoRepositorio.Agregar(documento);

        var version = new PlantillaDocumentoVersion(documento.Id, numeroVersion: 1, archivoUrl, hash);
        versionRepositorio.Agregar(version);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await archivosPendientes.QuitarTrasConfirmarAsync(archivoUrl, logger);

        return Result.Exito(new CrearPlantillaDocumentoResultadoDto(documento.Id, version.Id));
    }

    private static string CalcularHash(byte[] contenido) => Convert.ToHexStringLower(SHA256.HashData(contenido));
}
