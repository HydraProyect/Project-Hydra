using CaeManager.Application.Common;
using CaeManager.Application.Importacion.Maestra;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.Importacion.Queries;

/// <summary>
/// Analiza la plantilla maestra sin escribir. Devuelve <c>YaExiste</c> por CIF e identidad de todo el Tenant propietario, así que
/// exige Administrador del Tenant por el rol efectivo (<see cref="ICurrentUserService.ObtenerRolEfectivoAsync"/> comparado con
/// «Administrador», como los demás actos solo de Administrador; hasta ADR-013 M2 lo preguntaba a la autoridad de Inbound, K-2,
/// con el mismo predicado): no usa <see cref="IAutorizacionEscrituraEfectiva"/>, que concede también a una Sesión Privilegiada de Aprovisionamiento y
/// solo es seguro tras los behaviors de un <c>IComandoDeAprovisionamiento</c>; una Sesión Privilegiada no concede sobre lecturas de
/// Tenant. El cupo del limitador de análisis lo reserva la página (I-2), como en el resto de plantillas.
/// </summary>
[AreaDelEncargo("Importacion")]
public record AnalizarPlantillaMaestraQuery(byte[] ContenidoArchivo) : IRequest<Result<PlanImportacionMaestraDto>>, Common.ILecturaRegistradaEnSesionPrivilegiada;

public class AnalizarPlantillaMaestraQueryHandler(IPlantillaMaestraService servicio, ICurrentUserService currentUserService)
    : IRequestHandler<AnalizarPlantillaMaestraQuery, Result<PlanImportacionMaestraDto>>
{
    // Application no puede referenciar Infrastructure.Identity.Roles: mismo motivo que en AutorizacionEscrituraBehavior.
    private const string RolAdministrador = "Administrador";

    public async Task<Result<PlanImportacionMaestraDto>> Handle(AnalizarPlantillaMaestraQuery request, CancellationToken cancellationToken)
    {
        if (await currentUserService.ObtenerRolEfectivoAsync() != RolAdministrador)
            return Result.Fallo<PlanImportacionMaestraDto>(Error.Crear(
                "Importacion.SoloAdministrador", "Solo Administrador puede analizar la plantilla maestra."));

        using var stream = new MemoryStream(request.ContenidoArchivo);
        return await servicio.AnalizarAsync(stream, cancellationToken);
    }
}
