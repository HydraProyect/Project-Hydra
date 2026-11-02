using CaeManager.Application.Common;
using CaeManager.Application.Importacion.Maestra;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.Importacion.Queries;

/// <summary>
/// Analiza la plantilla maestra sin escribir. Devuelve <c>YaExiste</c> por CIF e identidad de todo el Tenant propietario, así que
/// pasa por la misma autorización que la confirmación (<see cref="IAutorizacionEscrituraEfectiva.EsActoDeAdministradorAsync"/>):
/// quien no puede importar no sondea qué identidades existen. El cupo del limitador de análisis lo reserva la página (I-2), como
/// en el resto de plantillas.
/// </summary>
public record AnalizarPlantillaMaestraQuery(byte[] ContenidoArchivo) : IRequest<Result<PlanImportacionMaestraDto>>;

public class AnalizarPlantillaMaestraQueryHandler(IPlantillaMaestraService servicio, IAutorizacionEscrituraEfectiva autorizacionEscrituraEfectiva)
    : IRequestHandler<AnalizarPlantillaMaestraQuery, Result<PlanImportacionMaestraDto>>
{
    public async Task<Result<PlanImportacionMaestraDto>> Handle(AnalizarPlantillaMaestraQuery request, CancellationToken cancellationToken)
    {
        if (!await autorizacionEscrituraEfectiva.EsActoDeAdministradorAsync(cancellationToken))
            return Result.Fallo<PlanImportacionMaestraDto>(Error.Crear(
                "Importacion.SoloAdministrador", "Solo Administrador puede analizar la plantilla maestra."));

        using var stream = new MemoryStream(request.ContenidoArchivo);
        return await servicio.AnalizarAsync(stream, cancellationToken);
    }
}
