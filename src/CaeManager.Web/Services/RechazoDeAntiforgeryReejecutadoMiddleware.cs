using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;

namespace CaeManager.Web.Services;

/// <summary>
/// Un POST de formulario sin token antiforgery válido recibe su 4xx (400) sin detalle, no un 500.
///
/// <para>
/// Causa medida (ZAP autenticado, run 41250723975: 1.131 respuestas 500 en los POST de
/// <c>/cuenta/cliente-activo</c>, <c>/cuenta/extension/token</c>, <c>/cuenta/idioma</c>,
/// <c>/cuenta/vista-demo</c> y <c>/cuenta/vista-vocabulario</c>): el endpoint rechaza bien con 400,
/// pero <c>UseStatusCodePagesWithReExecute("/not-found")</c> reejecuta el pipeline entero —con el
/// mismo método POST y los mismos <i>features</i>— y <c>UseAntiforgery</c> vuelve a validar. Esa
/// segunda validación lee <c>Request.HasFormContentType</c>, y el framework lanza
/// <see cref="InvalidOperationException"/> cuando el <c>IAntiforgeryValidationFeature</c> que dejó
/// la primera pasada es inválido y nadie lo ha consultado. El manejador de excepciones reejecuta
/// <c>/Error</c> por el mismo pipeline, vuelve a lanzar y la respuesta final es un 500 vacío.
/// </para>
///
/// <para>
/// Qué hace: en una reejecución de código de estado de una petición cuyo antiforgery ya fue
/// rechazado, no deja que <c>UseAntiforgery</c> ni los endpoints de la página de error vean la
/// petición otra vez. Responde con el código original y sin cuerpo (la reejecución restaura el
/// código al terminar). No acepta nada que antes se rechazara: el rechazo sigue siendo el
/// resultado, y la validación de la primera pasada no se toca. Va inmediatamente antes de
/// <c>UseAntiforgery</c>.
/// </para>
///
/// <para>
/// El rechazo se registra una vez, con método, ruta y tipo de fallo: sin cuerpo del formulario,
/// sin cadena de consulta, sin cookies, sin cabeceras y sin identidad del usuario.
/// </para>
/// </summary>
public class RechazoDeAntiforgeryReejecutadoMiddleware(RequestDelegate siguiente, ILogger<RechazoDeAntiforgeryReejecutadoMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext contexto)
    {
        if (contexto.Features.Get<IStatusCodeReExecuteFeature>() is { } reejecucion
            && contexto.Features.Get<IAntiforgeryValidationFeature>() is { IsValid: false } rechazo)
        {
            logger.LogWarning(
                "Petición rechazada por antiforgery: {Metodo} {Ruta} ({TipoDeFallo}, código {CodigoOriginal})",
                contexto.Request.Method,
                reejecucion.OriginalPathBase + reejecucion.OriginalPath,
                rechazo.Error?.GetType().Name,
                reejecucion.OriginalStatusCode);
            return;
        }

        await siguiente(contexto);
    }
}

public static class RechazoDeAntiforgeryReejecutadoMiddlewareExtensions
{
    /// <summary>
    /// Registrar justo antes de <c>UseAntiforgery</c> y después de <c>UseStatusCodePagesWithReExecute</c>
    /// (es esa reejecución la que reentra en el pipeline).
    /// </summary>
    public static IApplicationBuilder UseRechazoDeAntiforgeryReejecutado(this IApplicationBuilder app) =>
        app.UseMiddleware<RechazoDeAntiforgeryReejecutadoMiddleware>();
}
