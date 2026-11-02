using CaeManager.Application.AutorizacionesLectura;
using CaeManager.Application.Common;
using CaeManager.Application.Importacion.Maestra;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.Importacion.Queries;

/// <summary>
/// Analiza la plantilla maestra sin escribir. Devuelve <c>YaExiste</c> por CIF e identidad de todo el Tenant propietario, así que
/// exige Administrador del Tenant por el predicado de lectura (<see cref="AutorizacionLecturaAutoridad.EsAdministradorAsync"/>, rol
/// efectivo): no usa <see cref="IAutorizacionEscrituraEfectiva"/>, que concede también a una Sesión Privilegiada de Aprovisionamiento y
/// solo es seguro tras los behaviors de un <c>IComandoDeAprovisionamiento</c>; una Sesión Privilegiada no concede sobre lecturas de
/// Tenant. El cupo del limitador de análisis lo reserva la página (I-2), como en el resto de plantillas.
/// </summary>
public record AnalizarPlantillaMaestraQuery(byte[] ContenidoArchivo) : IRequest<Result<PlanImportacionMaestraDto>>;

public class AnalizarPlantillaMaestraQueryHandler(IPlantillaMaestraService servicio, ICurrentUserService currentUserService)
    : IRequestHandler<AnalizarPlantillaMaestraQuery, Result<PlanImportacionMaestraDto>>
{
    public async Task<Result<PlanImportacionMaestraDto>> Handle(AnalizarPlantillaMaestraQuery request, CancellationToken cancellationToken)
    {
        if (!await AutorizacionLecturaAutoridad.EsAdministradorAsync(currentUserService))
            return Result.Fallo<PlanImportacionMaestraDto>(Error.Crear(
                "Importacion.SoloAdministrador", "Solo Administrador puede analizar la plantilla maestra."));

        using var stream = new MemoryStream(request.ContenidoArchivo);
        return await servicio.AnalizarAsync(stream, cancellationToken);
    }
}
