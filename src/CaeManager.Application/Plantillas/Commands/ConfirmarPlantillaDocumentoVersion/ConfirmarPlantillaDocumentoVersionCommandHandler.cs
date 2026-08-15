using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plantillas;
using MediatR;

namespace CaeManager.Application.Plantillas.Commands.ConfirmarPlantillaDocumentoVersion;

public class ConfirmarPlantillaDocumentoVersionCommandHandler(
    IPlantillaDocumentoVersionRepository versionRepositorio,
    IPlantillaDocumentoRepository documentoRepositorio,
    ICurrentUserService usuarioActual,
    IUnitOfWork unitOfWork)
    : IRequestHandler<ConfirmarPlantillaDocumentoVersionCommand, Result>
{
    public async Task<Result> Handle(ConfirmarPlantillaDocumentoVersionCommand request, CancellationToken cancellationToken)
    {
        var version = await versionRepositorio.ObtenerPorIdAsync(request.PlantillaDocumentoVersionId, cancellationToken);
        if (version is null)
            return Result.Fallo(Error.Crear("Plantilla.VersionNoEncontrada", "No encontramos esta versión de plantilla."));

        if (version.EstadoConfiguracion == EstadoConfiguracionPlantilla.Confirmada)
            return Result.Fallo(Error.Crear("Plantilla.VersionYaConfirmada", "Esta versión ya está confirmada."));

        if (version.Elementos.Count == 0)
            return Result.Fallo(Error.Crear(
                "Plantilla.SinElementos", "Añade al menos un campo antes de confirmar la plantilla."));

        var usuarioId = await usuarioActual.ObtenerUsuarioActualIdAsync();
        if (usuarioId is not { } idUsuario)
            return Result.Fallo(Error.Crear("Plantilla.SinUsuarioActual", "No pudimos identificar quién confirma esta plantilla."));

        var documento = await documentoRepositorio.ObtenerPorIdAsync(version.PlantillaDocumentoId, cancellationToken);
        if (documento is null)
            return Result.Fallo(Error.Crear("Plantilla.NoEncontrada", "No encontramos la plantilla de esta versión."));

        version.Confirmar(idUsuario, DateTime.UtcNow);
        documento.EstablecerVersionActual(version.Id);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
