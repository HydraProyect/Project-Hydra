using System.Globalization;
using CaeManager.Application.Common;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Services;

/// <summary>
/// <c>Observabilidad:ServerTiming</c>. Apagado por defecto (appsettings.json); en
/// desarrollo lo enciende launchSettings.json y en staging o producción una
/// variable de entorno (<c>Observabilidad__ServerTiming__Habilitado=true</c>).
/// Gobierna a la vez la cabecera de las respuestas SSR y el registro por carga
/// de página del circuito interactivo.
/// </summary>
public sealed class OpcionesServerTiming
{
    public const string Seccion = "Observabilidad:ServerTiming";

    public bool Habilitado { get; set; }

    /// <summary>
    /// En el circuito no hay respuesta que cierre la carga: se da por terminada en
    /// la siguiente navegación, al cerrarse el circuito o, como muy tarde, tras
    /// esta ventana desde que empezó (lo que pase antes). Así lo que el usuario
    /// hace después en la página no se mezcla con lo que costó cargarla.
    /// </summary>
    public int VentanaCargaCircuitoSegundos { get; set; } = 5;
}

/// <summary>
/// Formato de la cabecera <c>Server-Timing</c> (W3C Server Timing) a partir de
/// una <see cref="InstantaneaCosteCarga"/>. Solo números y nombres fijos: nunca
/// ids, Tenant, ruta ni SQL (OWASP ASVS 4.0.3 V14.3). Los tiempos van en
/// <c>dur</c> (milisegundos); los recuentos en <c>desc</c>, para que las
/// herramientas del navegador no los pinten como milisegundos.
/// </summary>
public static class CabeceraServerTiming
{
    public const string Nombre = "Server-Timing";

    public static string Formatear(InstantaneaCosteCarga coste) => string.Create(CultureInfo.InvariantCulture,
        $"db;dur={coste.BaseDatos.TotalMilliseconds:0.0}, cmd;desc=\"{coste.Comandos}\", conn;desc=\"{coste.Conexiones}\", "
        + $"puerta;dur={coste.EsperaPuerta.TotalMilliseconds:0.0}, med;desc=\"{coste.Despachos}\"");
}

/// <summary>
/// Añade <c>Server-Timing</c> con el <see cref="CosteCarga"/> de la petición a las
/// respuestas HTML (las páginas renderizadas en el servidor) de un usuario
/// autenticado, cuando <see cref="OpcionesServerTiming.Habilitado"/> está activo.
///
/// <para>
/// La cabecera se escribe en <c>Response.OnStarting</c>, justo antes de enviar
/// las cabeceras: recoge todo lo que la petición gastó hasta ese momento,
/// middlewares incluidos. Una página con renderizado en streaming envía las
/// cabeceras antes de terminar de cargar, y en ese caso el valor se queda en lo
/// gastado hasta el primer envío.
/// </para>
///
/// <para>
/// El acumulador se toma al entrar, del scope de la petición original, y la
/// cabecera se registra una sola vez por <see cref="HttpContext"/>: cuando
/// <c>UseExceptionHandler</c> o <c>UseStatusCodePagesWithReExecute</c>
/// re-ejecutan la canalización con un scope nuevo, la cabecera cuenta el coste
/// del scope original (la petición que falló), no el del scope de la página de
/// error, y no sale duplicada.
/// </para>
///
/// <para>
/// Divulgación de información a un cliente autenticado (OWASP ASVS 4.0.3
/// V14.3): por eso va apagada por defecto, solo a usuarios autenticados y solo
/// con números agregados de la propia petición.
/// </para>
/// </summary>
public sealed class ServerTimingCosteCargaMiddleware(RequestDelegate siguiente, IOptions<OpcionesServerTiming> opciones)
{
    private static readonly object MarcaRegistrada = new();

    public Task InvokeAsync(HttpContext contexto)
    {
        if (opciones.Value.Habilitado && !contexto.Items.ContainsKey(MarcaRegistrada))
        {
            // Una medición no puede tumbar la petición: si no se puede resolver el acumulador o
            // registrar el OnStarting (respuesta ya empezada), la petición sigue sin cabecera.
            try
            {
                if (contexto.RequestServices?.GetService<CosteCarga>() is { } coste)
                {
                    contexto.Items[MarcaRegistrada] = true;
                    contexto.Response.OnStarting(EscribirCabecera, new Estado(contexto, coste));
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or NotSupportedException)
            {
            }
        }

        return siguiente(contexto);
    }

    private sealed record Estado(HttpContext Contexto, CosteCarga Coste);

    private static Task EscribirCabecera(object estado)
    {
        var (contexto, coste) = (Estado)estado;
        try
        {
            // La decisión se toma aquí y no al entrar: este middleware va antes de UseAuthentication,
            // y es al empezar la respuesta cuando el usuario, el endpoint y el tipo ya están fijados.
            if (contexto.User.Identity?.IsAuthenticated == true
                && EsPaginaSsr(contexto)
                && EsHtml(contexto.Response.ContentType))
            {
                contexto.Response.Headers.Append(CabeceraServerTiming.Nombre, CabeceraServerTiming.Formatear(coste.Leer()));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or NotSupportedException)
        {
            // Cabeceras ya de solo lectura o contexto liberado: la respuesta sale sin la cabecera.
        }

        return Task.CompletedTask;
    }

    /// <summary>Una página de componentes (SSR), no un endpoint de API, un fichero o un HTML de otra fuente.</summary>
    private static bool EsPaginaSsr(HttpContext contexto) =>
        contexto.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Components.Endpoints.ComponentTypeMetadata>() is not null;

    /// <summary>Tipo MIME exacto <c>text/html</c>, con o sin parámetros; no <c>text/html-algo</c>.</summary>
    private static bool EsHtml(string? tipo) =>
        Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(tipo, out var valor)
        && valor.MediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase);
}

public static class ServerTimingCosteCargaExtensions
{
    public static IApplicationBuilder UseServerTimingCosteCarga(this IApplicationBuilder app) =>
        app.UseMiddleware<ServerTimingCosteCargaMiddleware>();
}
