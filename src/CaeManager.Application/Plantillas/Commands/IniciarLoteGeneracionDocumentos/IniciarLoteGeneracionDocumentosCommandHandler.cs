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
    IAlcanceDatosService alcanceDatos,
    IUnitOfWork unitOfWork)
    : IRequestHandler<IniciarLoteGeneracionDocumentosCommand, Result<IniciarLoteGeneracionDocumentosResultadoDto>>
{
    public async Task<Result<IniciarLoteGeneracionDocumentosResultadoDto>> Handle(
        IniciarLoteGeneracionDocumentosCommand request, CancellationToken cancellationToken)
    {
        var version = await versionRepositorio.ObtenerPorIdAsync(request.PlantillaDocumentoVersionId, cancellationToken);
        if (version is null)
            return VersionNoEncontrada();

        var plantilla = await plantillaRepositorio.ObtenerPorIdAsync(version.PlantillaDocumentoId, cancellationToken);
        if (plantilla is null)
            return Fallo("Plantilla.NoEncontrada", "No encontramos la plantilla de esta versión.");

        // Asignación de Cartera, antes que cualquier otra validación de la versión: una plantilla atada a un Centro o a
        // un Cliente empresarial fuera del alcance responde igual que una versión que no existe (ver AlcanceDePlantillas).
        if (!await alcanceDatos.PlantillaDentroDeAlcanceAsync(plantilla, cancellationToken))
            return VersionNoEncontrada();

        if (version.EstadoConfiguracion != EstadoConfiguracionPlantilla.Confirmada)
            return Fallo("Plantilla.VersionNoConfirmada", "Esta versión de plantilla todavía no está confirmada.");
        if (plantilla.AmbitoAplicacion != AmbitoAplicacion.Trabajador)
            return Fallo("Plantilla.LoteSoloTrabajador", "La generación en lote solo está disponible para plantillas de ámbito Trabajador.");

        var usuarioId = await usuarioActual.ObtenerUsuarioActualIdAsync();
        if (usuarioId is not { } idUsuario)
            return Fallo("Plantilla.SinUsuarioActual", "No pudimos identificar quién solicita este lote.");

        var trabajadorIds = request.TrabajadorIds.Distinct().ToList();

        // Todos los Trabajadores del lote tienen que estar en el alcance de quien lo pide; si falta uno, no nace el lote
        // (ni a medias), con el mismo error con el que GenerarDocumentoIndividual rechaza un Trabajador que no alcanza.
        var trabajadoresVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        if (trabajadoresVisibles is not null)
        {
            var visibles = trabajadoresVisibles.ToHashSet();
            if (!trabajadorIds.All(visibles.Contains))
                return Fallo("Plantilla.PropietarioNoEncontrado", "No encontramos a quién pertenece este documento.");
        }

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

    private static Result<IniciarLoteGeneracionDocumentosResultadoDto> VersionNoEncontrada() =>
        Fallo("Plantilla.VersionNoEncontrada", "No encontramos esta versión de plantilla.");

    private static Result<IniciarLoteGeneracionDocumentosResultadoDto> Fallo(string codigo, string mensaje) =>
        Result.Fallo<IniciarLoteGeneracionDocumentosResultadoDto>(Error.Crear(codigo, mensaje));
}
