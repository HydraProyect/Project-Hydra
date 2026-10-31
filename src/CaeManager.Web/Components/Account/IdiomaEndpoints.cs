using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Recursos;
using CaeManager.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Components.Account;

/// <summary>
/// Cambia el idioma de la interfaz de la cuenta (<c>ApplicationUser.Idioma</c>).
/// Endpoint HTTP y no el patrón JS de <c>SelectorTema</c>: el tema tiene que
/// cambiar sin petición HTTP (REC-137), el idioma no — y cambiar de cultura
/// exige de todos modos un circuito de Blazor nuevo, que llega con la
/// redirección. Sin Command de Application por el mismo motivo que
/// <c>SelectorTema</c>: <c>ApplicationUser</c> vive en Infrastructure, que
/// Application no puede ver (<c>FronterasDeCapaTests</c>).
///
/// <para>
/// Invariante: <b>la cookie nunca se adelanta al estado persistido</b>. Solo
/// se escribe tras una escritura confirmada; si la cuenta no llega a
/// guardarse, la respuesta no aparenta éxito y la cookie no cambia.
/// </para>
///
/// <para>
/// Con el catalán apagado (<see cref="OpcionesLocalizacion"/>), <c>ca-ES</c>
/// se rechaza igual que una cultura desconocida: sin tocar la cuenta ni la
/// cookie.
/// </para>
/// </summary>
public static class IdiomaEndpoints
{
    public const string Ruta = "/cuenta/idioma";

    public static IEndpointRouteBuilder MapIdiomaEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // POST de formulario: UseAntiforgery valida el token que el propio
        // formulario envía (SelectorIdioma), igual que /cuenta/cliente-activo.
        endpoints.MapPost(Ruta, (
            [FromForm] string? idioma, [FromForm] string? returnUrl, HttpContext httpContext,
            IPreferenciasCuentaPropia preferencias,
            IStringLocalizer<TextosComunes> textos,
            IOptions<OpcionesLocalizacion> localizacion,
            ILoggerFactory loggerFactory) =>
            CambiarAsync(idioma, returnUrl, httpContext, preferencias, textos, localizacion.Value,
                loggerFactory.CreateLogger(typeof(IdiomaEndpoints).FullName!)))
            .RequireAuthorization();

        return endpoints;
    }

    /// <summary>El manejador de <see cref="Ruta"/>; público para que los tests lo ejerciten tal cual.</summary>
    public static async Task<IResult> CambiarAsync(
        string? idioma, string? returnUrl, HttpContext httpContext,
        IPreferenciasCuentaPropia preferencias,
        IStringLocalizer<TextosComunes> textos, OpcionesLocalizacion localizacion, ILogger logger)
    {
        if (!Guid.TryParse(httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), out var usuarioId))
            return Results.Unauthorized();

        // Lista blanca: un valor fuera de las culturas soportadas no se
        // traduce a español en silencio, se rechaza; tampoco uno soportado
        // pero apagado por configuración (el catalán, hoy).
        if (!CulturaUsuarioCookie.IntentarDesdeCultura(idioma, out var idiomaElegido)
            || !CulturaUsuarioCookie.EstaActivo(idiomaElegido, localizacion))
            return Results.BadRequest();

        var destino = RedireccionLocal.SanearParaVolver(returnUrl);

        if (!await GuardarAsync(preferencias, logger, usuarioId, idiomaElegido))
            return RespuestaNoCambiado(textos, destino);

        CulturaUsuarioCookie.Escribir(httpContext, idiomaElegido);
        return Results.LocalRedirect(destino);
    }

    /// <summary>
    /// Mismo tratamiento que <c>SelectorTema.GuardarTemaAsync</c>:
    /// <c>ActividadUsuarioService</c> actualiza esta misma fila en cada carga
    /// de página, así que entre la lectura y la escritura puede colarse otra
    /// escritura. <see cref="IPreferenciasCuentaPropia"/> relee la fila y
    /// reaplica SOLO el idioma, una única vez. Devuelve <c>true</c> solo si
    /// algún intento persistió.
    /// </summary>
    public static async Task<bool> GuardarAsync(
        IPreferenciasCuentaPropia preferencias, ILogger logger, Guid usuarioId, IdiomaPreferido idioma)
    {
        var resultado = await preferencias.GuardarIdiomaAsync(usuarioId, idioma);
        if (resultado == ResultadoEscrituraCuenta.Guardada) return true;

        logger.LogWarning(
            "No se pudo guardar el idioma elegido para {UsuarioId}: {Resultado}", usuarioId, resultado);
        return false;
    }

    /// <summary>
    /// 409 con una página mínima, no una redirección: redirigir a la página de
    /// origen aparentaría que el cambio se hizo. La página sale en la cultura
    /// de esta petición —la anterior, que es la que sigue vigente—.
    /// </summary>
    private static IResult RespuestaNoCambiado(IStringLocalizer<TextosComunes> textos, string destino)
    {
        var html = HtmlEncoder.Default;
        var cuerpo =
            $"""
            <!DOCTYPE html>
            <html lang="{html.Encode(System.Globalization.CultureInfo.CurrentUICulture.Name)}">
            <head><meta charset="utf-8" /><title>{html.Encode(textos["IdiomaNoCambiadoTitulo"])}</title></head>
            <body>
            <h1>{html.Encode(textos["IdiomaNoCambiadoTitulo"])}</h1>
            <p>{html.Encode(textos["IdiomaNoCambiadoDetalle"])}</p>
            <p><a href="{html.Encode(destino)}">{html.Encode(textos["Volver"])}</a></p>
            </body>
            </html>
            """;
        return Results.Content(cuerpo, "text/html", Encoding.UTF8, StatusCodes.Status409Conflict);
    }
}
