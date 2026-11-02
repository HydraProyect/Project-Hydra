using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Domain.ApiKeys;
using CaeManager.Domain.Common;

namespace CaeManager.Web.Api.V1;

/// <summary>
/// Rastro agregado de <c>/api/v1</c> (C-11, decisión D-5 opción a). Va detrás del filtro de alcance: lo
/// denegado no ha leído nada. Y va antes de entregar datos, con fallo cerrado: si la fila no se puede
/// escribir la excepción sube y no sale nada. Por eso cuenta la lectura autorizada, no la respuesta
/// correcta: un 404 o un error posterior también suman.
/// <para>
/// Los servicios salen de <c>RequestServices</c>: <c>AddEndpointFilter</c> crea el filtro una vez, con el
/// proveedor raíz, y el contador usa el contexto de base de datos de la petición.
/// </para>
/// </summary>
public sealed class RastroUsoApiEndpointFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var servicios = http.RequestServices;

        var tenantId = servicios.GetRequiredService<ITenantActual>().TenantId
            ?? throw new InvalidOperationException("Una petición de la API v1 llegó sin Tenant resuelto.");
        if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var apiKeyId))
            throw new InvalidOperationException("Una petición de la API v1 llegó sin identificador de clave.");
        var endpoint = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText
            ?? throw new InvalidOperationException("Una petición de la API v1 llegó sin patrón de ruta.");

        await servicios.GetRequiredService<IRastroUsoApi>().RegistrarPeticionAsync(
            tenantId, apiKeyId,
            ApiV1.EsClaveDeSoporte(http.User) ? TipoClaveUsoApi.ClaveSoporte : TipoClaveUsoApi.ClaveIntegracion,
            DiaDeNegocio.Hoy(), endpoint, http.RequestAborted);

        return await next(context);
    }
}
