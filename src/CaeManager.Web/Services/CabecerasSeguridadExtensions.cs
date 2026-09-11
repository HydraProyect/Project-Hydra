using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Components;

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
    // El único <script> inline que sirve el propio framework es el
    // componente <ImportMap /> de App.razor (H6, docs/ux-audit/
    // 16-transversales.md — "Executing inline script violates CSP" en cada
    // navegación, atribuido aquí): el mapa de imports con el fingerprint de
    // cada .razor.js/.js de la app (QuickGrid, ApexCharts, ReconnectModal,
    // los módulos propios en wwwroot/js). Un <script type="importmap"> no
    // admite src externo de forma fiable entre navegadores, así que ASP.NET
    // Core siempre lo renderiza inline — la única forma de permitirlo sin
    // 'unsafe-inline' es fijar el hash de su contenido exacto.
    //
    // Ese contenido cambia cada vez que se añade, quita o edita un .js/
    // .razor.js de la app, así que un hash fijado a mano (un string
    // constante) queda obsoleto en silencio en cuanto cambia el árbol — con
    // el navegador bloqueando el importmap en cada página sin que ningún
    // build ni test lo note (ocurrió de verdad: el hash medido en un
    // checkout Windows con CRLF tampoco coincide con el que sirve
    // producción, que compila sobre LF, y ni siquiera coincide entre
    // endpoints — <ImportMapDefinition> se resuelve por endpoint). Por eso
    // NO se fija a mano: se lee, para cada petición, el mismo
    // <see cref="ImportMapDefinition"/> que el propio componente
    // <c>&lt;ImportMap /&gt;</c> resuelve para ESE endpoint
    // (<c>HttpContext.GetEndpoint().Metadata.GetMetadata&lt;ImportMapDefinition&gt;()</c>,
    // literalmente la misma llamada que hace <c>ImportMap.SetParametersAsync</c>
    // en el framework) y se hashea su <c>ToString()</c> — que reenvía al
    // mismo <c>ToJson()</c> interno que usa <ImportMap /> para renderizar —
    // así que el hash SIEMPRE coincide con lo que el navegador va a recibir
    // en esa página, en cualquier entorno.
    private static readonly ConcurrentDictionary<ImportMapDefinition, string> _hashesImportMapPorDefinicion = new();

    public static IApplicationBuilder UseCabecerasSeguridad(this IApplicationBuilder app)
    {
        return app.Use(async (contexto, siguiente) =>
        {
            var cabeceras = contexto.Response.Headers;

            cabeceras["Content-Security-Policy"] = ConstruirPoliticaSeguridadContenido(contexto);
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

            // Por defecto nada se cachea: todo lo que no pasa por aquí como
            // caso especial va contra MediatR con datos de tenant y no debe
            // quedar en ninguna caché intermedia ni en el disco del navegador
            // (hallazgo del Módulo 9, auditoría 2026-08-30 — ninguna respuesta
            // autenticada declaraba `no-store`). Se fija ANTES de llamar a
            // `siguiente()`: `app.MapStaticAssets()` sirve los activos con
            // fingerprint (css/js/imágenes) más abajo en el pipeline y pone su
            // propio `Cache-Control` de larga duración, que pisa este valor
            // por defecto sin que este middleware tenga que conocer sus rutas.
            cabeceras["Cache-Control"] = "no-store, private";
            cabeceras["Pragma"] = "no-cache";

            await siguiente();
        });
    }

    private static string ConstruirPoliticaSeguridadContenido(HttpContext contexto)
    {
        return
            "default-src 'self'; " +
            $"{ConstruirDirectivaScriptSrc(contexto)}; " +
            "style-src 'self' 'unsafe-inline'; " +
            "img-src 'self' data:; " +
            "font-src 'self'; " +
            "connect-src 'self'; " +
            "object-src 'none'; " +
            "base-uri 'self'; " +
            "form-action 'self'; " +
            "frame-ancestors 'none'";
    }

    /// <summary>
    /// La misma resolución que hace <c>ImportMap.SetParametersAsync</c> del
    /// framework: si el endpoint actual no tiene <see cref="ImportMapDefinition"/>
    /// en sus metadatos (rutas que no son una página Razor — /salud,
    /// /api/v1/..., los propios activos estáticos), esa página no va a
    /// renderizar ningún <c>&lt;ImportMap /&gt;</c>, así que no hace falta
    /// ningún hash en <c>script-src</c>.
    /// </summary>
    private static string ConstruirDirectivaScriptSrc(HttpContext contexto)
    {
        var definicion = contexto.GetEndpoint()?.Metadata.GetMetadata<ImportMapDefinition>();
        if (definicion is null)
        {
            return "script-src 'self'";
        }

        var hash = _hashesImportMapPorDefinicion.GetOrAdd(definicion, CalcularHashImportMap);
        return $"script-src 'self' 'sha256-{hash}'";
    }

    private static string CalcularHashImportMap(ImportMapDefinition definicion)
    {
        // ImportMapDefinition.ToJson() indenta con Utf8JsonWriter, cuyo salto
        // de línea (\r\n en este SDK/plataforma) es un detalle de
        // implementación, no algo que el navegador respete: al parsear el
        // HTML, el "preprocessing the input stream" del propio estándar
        // (https://html.spec.whatwg.org/multipage/parsing.html#preprocessing-the-input-stream)
        // normaliza TODO salto de línea a \n antes de que el motor calcule el
        // hash del <script> — el mismo texto que ejecuta. Sin esta
        // normalización el hash que calculamos aquí no es el que el
        // navegador exige, con independencia del sistema operativo donde
        // corra la app (reproducido en local: 1566 bytes con \r\n contra los
        // 1544 que ve el navegador).
        var json = NormalizarSaltosDeLinea(definicion.ToString()!);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToBase64String(hash);
    }

    private static string NormalizarSaltosDeLinea(string texto) =>
        texto.Replace("\r\n", "\n").Replace("\r", "\n");
}
