namespace CaeManager.Web.Services;

/// <summary>
/// Content-Security-Policy y cabeceras de endurecimiento (hallazgo N-1 de
/// INFORME-AUDITORIA-2.md). Es la segunda línea detrás del saneado de
/// <c>ISanitizadorHtmlService</c>: el saneado impide que entre marcado
/// peligroso, la CSP limita el daño si alguna vez entra por una vía que no
/// hayamos previsto.
/// </summary>
public static class CabecerasSeguridadExtensions
{
    // script-src sin 'unsafe-inline' a propósito: es lo único que hace que
    // una CSP sirva de algo contra XSS. Los @onclick/@onchange de Blazor no
    // son manejadores HTML inline (los registra el propio framework desde
    // blazor.web.js), así que no dependen de ello.
    //
    // style-src sí lleva 'unsafe-inline', y es deuda consciente: quedan ~16
    // atributos style="…" repartidos por las páginas (el patrón
    // style="display:@(…)" de las listas paginadas). Migrarlos es un refactor
    // de UI independiente de esta auditoría. El riesgo residual es acotado:
    // el sanitizador ya elimina la etiqueta <style> y los atributos style y
    // class del HTML de correo, que es la entrada no confiable real.
    //
    // connect-src 'self' cubre el WebSocket de SignalR del circuito Blazor
    // Server — 'self' incluye ws/wss del mismo origen.
    private const string PoliticaSeguridadContenido =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "connect-src 'self'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'";

    public static IApplicationBuilder UseCabecerasSeguridad(this IApplicationBuilder app)
    {
        return app.Use(async (contexto, siguiente) =>
        {
            var cabeceras = contexto.Response.Headers;

            cabeceras["Content-Security-Policy"] = PoliticaSeguridadContenido;
            cabeceras["X-Content-Type-Options"] = "nosniff";
            // Redundante con frame-ancestors para navegadores actuales, pero
            // es la única forma de decírselo a los que no aplican CSP 2.
            cabeceras["X-Frame-Options"] = "DENY";
            // Ninguna pantalla necesita estas capacidades; negarlas evita que
            // una inyección futura las pida en nombre del usuario.
            cabeceras["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            // Los identificadores de cliente/documento viajan en la ruta: no
            // deben salir del sistema en el Referer de un enlace externo.
            cabeceras["Referrer-Policy"] = "strict-origin-when-cross-origin";

            await siguiente();
        });
    }
}
