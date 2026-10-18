using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plantillas;
using MediatR;

namespace CaeManager.Application.Plantillas.Commands.GuardarElementosPlantilla;

public class GuardarElementosPlantillaCommandHandler(
    IPlantillaDocumentoVersionRepository versionRepositorio,
    IPlantillaDocumentoRepository documentoRepositorio,
    IAlcanceDatosService alcanceDatos,
    IUnitOfWork unitOfWork)
    : IRequestHandler<GuardarElementosPlantillaCommand, Result>
{
    public async Task<Result> Handle(GuardarElementosPlantillaCommand request, CancellationToken cancellationToken)
    {
        // Asignación de Cartera: una versión cuya plantilla está atada a un Centro o a un Cliente
        // empresarial fuera del alcance responde igual que una versión que no existe (ver
        // AlcanceDePlantillas). Una versión sin plantilla también: falla cerrado.
        var version = await versionRepositorio.ObtenerPorIdAsync(request.PlantillaDocumentoVersionId, cancellationToken);
        var documento = version is null ? null : await documentoRepositorio.ObtenerPorIdAsync(version.PlantillaDocumentoId, cancellationToken);
        if (version is null || documento is null || !await alcanceDatos.PlantillaDentroDeAlcanceAsync(documento, cancellationToken))
            return Result.Fallo(Error.Crear("Plantilla.VersionNoEncontrada", "No encontramos esta versión de plantilla."));

        if (version.EstadoConfiguracion == EstadoConfiguracionPlantilla.Confirmada)
            return Result.Fallo(Error.Crear(
                "Plantilla.VersionYaConfirmada", "Esta versión ya está confirmada — crea una versión nueva para modificarla."));

        var elementos = request.Elementos.Select(e => new PlantillaElemento(
            version.Id, e.Tipo, e.Pagina, e.X, e.Y, e.Ancho, e.Alto, e.EtiquetaVisible,
            e.FuenteDato, e.ValorConstante, e.Formato, e.Obligatorio, e.RolFirmante, e.NombreCampoAcroForm));

        version.EstablecerElementos(elementos);

        if (version.EstadoConfiguracion == EstadoConfiguracionPlantilla.Borrador)
            version.MarcarPendienteRevision();

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
