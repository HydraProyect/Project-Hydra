using CaeManager.Application.Importacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Services;

namespace CaeManager.Web.Features.Importacion;

/// <summary>Descarga de la plantilla maestra del Importador (Paso 1), enlazada solo desde /importacion.</summary>
public static class ImportacionEndpoints
{
    public const string RutaPlantillaMaestra = "/importacion/plantilla-maestra.xlsx";
    public const string NombrePlantillaMaestra = "PLANTILLA-MAESTRA-IMPORTADOR.xlsx";

    public static IEndpointRouteBuilder MapImportacionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Mismo patrón que /documentos/plantilla.xlsx: solo se enlaza desde /importacion (Importacion.razor: Roles =
        // ConCargaMasivaCsv: Administrador, Dirección CAE y Coordinador CAE), y es una plantilla en blanco sin datos de
        // Tenant. El análisis y la escritura de la plantilla maestra los autoriza Application, no este endpoint.
        endpoints.MapGet(RutaPlantillaMaestra, (IPlantillaMaestraService servicio) =>
            Results.File(
                servicio.GenerarPlantilla(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                NombrePlantillaMaestra))
        .RequireAuthorization(policy => policy.RequireRole(Roles.Administrador, Roles.DireccionCae, Roles.CoordinadorCae))
        .ExcluidoDelEncargoDeAdministracion();

        return endpoints;
    }
}
