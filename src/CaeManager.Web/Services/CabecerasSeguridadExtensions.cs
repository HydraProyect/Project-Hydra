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
    // El hash sha256 es el único <script> inline que sirve el propio
    // framework: el componente <ImportMap /> de App.razor (H6, docs/ux-audit/
    // 16-transversales.md — "Executing inline script violates CSP" en cada
    // navegación, atribuido aquí) — el mapa de imports con el fingerprint de
    // cada .razor.js/.js de la app (QuickGrid, ApexCharts, ReconnectModal,
    // los módulos propios en wwwroot/js). Un <script type="importmap"> no
    // admite src externo de forma fiable entre navegadores, así que ASP.NET
    // Core siempre lo renderiza inline — la única forma de permitirlo sin
    // 'unsafe-inline' es fijar el hash de su contenido exacto.
    // ADVERTENCIA: este hash cambia si cambia el conjunto de módulos JS de
    // la app (añadir/quitar un archivo .js o .razor.js, o una librería con
    // JS isolation) — un build con la CSP rota (mismo error en consola) es
    // la señal de que hay que recalcularlo.
    private const string PoliticaSeguridadContenido =
        "default-src 'self'; " +
        "script-src 'self' 'sha256-DrLXAMNvj/4Fwhv8YWHGxtNgNMz+MtjuFL2xo4B/uJw='; " +
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
