using System.Security.Cryptography;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plantillas;
using MediatR;

namespace CaeManager.Application.Plantillas.Commands.CrearPlantillaDocumento;

public class CrearPlantillaDocumentoCommandHandler(
    IPlantillaDocumentoRepository documentoRepositorio,
    IPlantillaDocumentoVersionRepository versionRepositorio,
    IFileStorageService almacenamientoArchivos,
    IAlcanceDatosService alcanceDatos,
    IUnitOfWork unitOfWork)
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
                return Result.Fallo<CrearPlantillaDocumentoResultadoDto>(Error.Crear("Plantilla.ClienteSinAcceso", "No encontramos este cliente."));
        }

        var hash = CalcularHash(request.Contenido);

        using var flujo = new MemoryStream(request.Contenido);
        var archivoUrl = await almacenamientoArchivos.GuardarAsync(flujo, request.NombreArchivo, cancellationToken);

        var documento = new PlantillaDocumento(
            OrigenPlantilla.Externa, request.Nombre, request.AmbitoAplicacion, request.FormatoOrigen,
            request.Descripcion, request.CentroId, request.ClienteId);
        documentoRepositorio.Agregar(documento);

        var version = new PlantillaDocumentoVersion(documento.Id, numeroVersion: 1, archivoUrl, hash);
        versionRepositorio.Agregar(version);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(new CrearPlantillaDocumentoResultadoDto(documento.Id, version.Id));
    }

    private static string CalcularHash(byte[] contenido) => Convert.ToHexStringLower(SHA256.HashData(contenido));
}
