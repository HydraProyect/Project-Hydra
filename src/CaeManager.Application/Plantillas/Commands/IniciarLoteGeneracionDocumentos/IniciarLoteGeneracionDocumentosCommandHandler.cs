using System.Text.Json;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Plantillas;
using MediatR;

namespace CaeManager.Application.Plantillas.Commands.IniciarLoteGeneracionDocumentos;

public class IniciarLoteGeneracionDocumentosCommandHandler(
    IPlantillaDocumentoVersionRepository versionRepositorio,
    IPlantillaDocumentoRepository plantillaRepositorio,
    ILoteGeneracionDocumentoRepository loteRepositorio,
    IItemGeneracionDocumentoRepository itemRepositorio,
    ICurrentUserService usuarioActual,
    IUnitOfWork unitOfWork)
    : IRequestHandler<IniciarLoteGeneracionDocumentosCommand, Result<IniciarLoteGeneracionDocumentosResultadoDto>>
{
    public async Task<Result<IniciarLoteGeneracionDocumentosResultadoDto>> Handle(
        IniciarLoteGeneracionDocumentosCommand request, CancellationToken cancellationToken)
    {
        var version = await versionRepositorio.ObtenerPorIdAsync(request.PlantillaDocumentoVersionId, cancellationToken);
        if (version is null)
            return Fallo("Plantilla.VersionNoEncontrada", "No encontramos esta versión de plantilla.");
        if (version.EstadoConfiguracion != EstadoConfiguracionPlantilla.Confirmada)
            return Fallo("Plantilla.VersionNoConfirmada", "Esta versión de plantilla todavía no está confirmada.");

        var plantilla = await plantillaRepositorio.ObtenerPorIdAsync(version.PlantillaDocumentoId, cancellationToken);
        if (plantilla is null)
            return Fallo("Plantilla.NoEncontrada", "No encontramos la plantilla de esta versión.");
        if (plantilla.AmbitoAplicacion != AmbitoAplicacion.Trabajador)
            return Fallo("Plantilla.LoteSoloTrabajador", "La generación en lote solo está disponible para plantillas de ámbito Trabajador.");

        var usuarioId = await usuarioActual.ObtenerUsuarioActualIdAsync();
        if (usuarioId is not { } idUsuario)
            return Fallo("Plantilla.SinUsuarioActual", "No pudimos identificar quién solicita este lote.");

        var trabajadorIds = request.TrabajadorIds.Distinct().ToList();
        var contextoJson = JsonSerializer.Serialize(new ContextoLoteGeneracionDto(
            request.CentroId, request.ValoresManuales?.ToDictionary(par => par.Key, par => par.Value) ?? []));

        var ahoraUtc = DateTime.UtcNow;
        var lote = new LoteGeneracionDocumento(version.Id, trabajadorIds.Count, idUsuario, ahoraUtc, contextoJson);
        loteRepositorio.Agregar(lote);

        var items = trabajadorIds.Select(trabajadorId => new ItemGeneracionDocumento(lote.Id, trabajadorId)).ToList();
        foreach (var item in items)
            itemRepositorio.Agregar(item);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(new IniciarLoteGeneracionDocumentosResultadoDto(lote.Id, items.Select(i => i.Id).ToList()));
    }

    private static Result<IniciarLoteGeneracionDocumentosResultadoDto> Fallo(string codigo, string mensaje) =>
        Result.Fallo<IniciarLoteGeneracionDocumentosResultadoDto>(Error.Crear(codigo, mensaje));
}
