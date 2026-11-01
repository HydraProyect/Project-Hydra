using System.Net.Http.Headers;
using CaeManager.Infrastructure.Autenticacion;
using CaeManager.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace CaeManager.Web.Api.V1;

/// <summary>
/// <c>POST /api/v1/token</c>: flujo <c>client_credentials</c> de la Clave de Integración del Tenant (D-3).
/// <c>client_id</c> es el Id de la clave y <c>client_secret</c> su secreto, por HTTP Basic. Todo fallo de
/// la credencial responde igual, sin decir si la clave existe, está revocada o el Tenant está cortado.
/// </summary>
public static class TokenIntegracionEndpoints
{
    private const int TamanoMaximoCuerpo = 1024;

    public static IEndpointRouteBuilder MapTokenIntegracionEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/token", async (HttpContext contexto, EmisorTokenClaveIntegracion emisor) =>
            {
                var cabeceras = contexto.Response.Headers;
                cabeceras.CacheControl = "no-store";
                cabeceras.Pragma = "no-cache";

                if (!contexto.Request.HasFormContentType)
                    return ErrorOAuth("invalid_request", StatusCodes.Status400BadRequest);

                var formulario = await contexto.Request.ReadFormAsync(contexto.RequestAborted);
                if (formulario["grant_type"] != "client_credentials")
                    return ErrorOAuth("unsupported_grant_type", StatusCodes.Status400BadRequest);

                var credenciales = CabeceraClaveIntegracion.ExtraerCredenciales(contexto.Request.Headers.Authorization.ToString());
                var token = credenciales is null ? null : await emisor.EmitirAsync(credenciales, contexto.RequestAborted);
                if (token is null)
                {
                    cabeceras.WWWAuthenticate = new AuthenticationHeaderValue("Basic", "realm=\"TALVEG\"").ToString();
                    return ErrorOAuth("invalid_client", StatusCodes.Status401Unauthorized);
                }

                return Results.Json(new Dictionary<string, object>
                {
                    ["access_token"] = token,
                    ["token_type"] = "Bearer",
                    ["expires_in"] = (int)TokenClaveIntegracion.Vigencia.TotalSeconds,
                });
            })
            .AllowAnonymous()
            .RequireRateLimiting(LimitadorApiPublica.PoliticaToken)
            .AddEndpointFilter<ActorIntegracionExternaEndpointFilter>()
            .WithMetadata(new RequestSizeLimitAttribute(TamanoMaximoCuerpo));

        return endpoints;
    }

    private static IResult ErrorOAuth(string error, int estado) =>
        Results.Json(new Dictionary<string, string> { ["error"] = error }, statusCode: estado);
}
