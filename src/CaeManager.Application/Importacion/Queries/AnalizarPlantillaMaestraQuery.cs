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
/// Como no consulta el alcance, el cierre del Tenant solo Inbound («Inbound sin Outbound», 2026-11-18) lo pregunta él mismo a
/// <see cref="ICriterioDeOutbound"/> con el papel activo: sin eso, su <c>YaExiste</c> diría qué Empresas, Centros y
/// Trabajadores de Outbound tiene el Tenant aunque la importación (un comando) ya se le niegue.
/// </summary>
[AreaDelEncargo("Importacion")]
public record AnalizarPlantillaMaestraQuery(byte[] ContenidoArchivo) : IRequest<Result<PlanImportacionMaestraDto>>, Common.ILecturaRegistradaEnSesionPrivilegiada;

public class AnalizarPlantillaMaestraQueryHandler(
    IPlantillaMaestraService servicio, ICurrentUserService currentUserService, ICriterioDeOutbound criterioDeOutbound)
    : IRequestHandler<AnalizarPlantillaMaestraQuery, Result<PlanImportacionMaestraDto>>
{
    // Application no puede referenciar Infrastructure.Identity.Roles: mismo motivo que en AutorizacionEscrituraBehavior.
    private const string RolAdministrador = "Administrador";

    public async Task<Result<PlanImportacionMaestraDto>> Handle(AnalizarPlantillaMaestraQuery request, CancellationToken cancellationToken)
    {
        if (await currentUserService.ObtenerRolEfectivoAsync() != RolAdministrador)
            return Result.Fallo<PlanImportacionMaestraDto>(Error.Crear(
                "Importacion.SoloAdministrador", "Solo Administrador puede analizar la plantilla maestra."));

        if (await criterioDeOutbound.CierraPorSoloInboundAsync(await currentUserService.ObtenerPapelActivoAsync(), cancellationToken))
            return Result.Fallo<PlanImportacionMaestraDto>(Error.Crear(
                "Importacion.SinLineaOutbound",
                "Tu organización tiene contratado Inbound y no Outbound: analizar la plantilla maestra es de Outbound."));

        using var stream = new MemoryStream(request.ContenidoArchivo);
        return await servicio.AnalizarAsync(stream, cancellationToken);
    }
}
