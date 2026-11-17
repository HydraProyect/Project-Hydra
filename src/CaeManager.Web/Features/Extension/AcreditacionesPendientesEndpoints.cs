using CaeManager.Application.Documentos.Queries.ObtenerAcreditacionesPorProveedor;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Services.Autorizacion;
using MediatR;

namespace CaeManager.Web.Features.Extension;

/// <summary>
/// Incremento 2 del MVP1 de integración con plataformas CAE externas vía
/// extensión de navegador (ver Project-Hydra-Negocio/tecnico/ARQUITECTURA-INTEGRACIONES.md § 14 en el
/// repositorio de negocio, y <see cref="ExtensionTokenEndpoints"/> para el
/// Incremento 1).
///
/// <para>
/// No es una query nueva: reexpone <see cref="ObtenerAcreditacionesPorProveedorQuery"/>
/// — ya construida para el drill-down de <c>PlataformaTab.razor</c> y para la
/// Bandeja del gestor — sin duplicar su alcance por cartera
/// (<c>IAlcanceDatosService</c>) ni su agrupación Proveedor → Cliente →
/// Documento. Excluye los rechazos vigentes solo para la extensión y no envía el DNI.
/// </para>
/// </summary>
public static class AcreditacionesPendientesEndpoints
{
    public static IEndpointRouteBuilder MapAcreditacionesPendientesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/extension/acreditaciones-pendientes", async (
            IMediator mediator, CancellationToken cancellationToken) =>
        {
            var proveedores = await mediator.Send(new ObtenerAcreditacionesPorProveedorQuery(SoloSubiblesExtension: true, IncluirDominios: true), cancellationToken);
            return Results.Ok(proveedores);
        })
        // Sesión interactiva o token de extensión, nunca clave de API de tenant. No
        // relaja nada — el alcance por cartera lo sigue imponiendo
        // IAlcanceDatosService dentro de la query con el rol real de quien
        // pide, que para la extensión es el del usuario que emitió el token.
        // Conserva el ámbito de gestión, también al minimizar los datos del DTO.
        .RequireAuthorization(Policies.SesionOExtension)
        // Con la sesión web y papel Inbound, se niega como las páginas de Outbound (O-1 (b)). Por el token, el papel es
        // el de la Asignación de Operación de Outbound que resuelve ContextoExtensionMiddleware, o el Tenant de origen.
        .DeOutbound();

        return endpoints;
    }
}
