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
/// Divulgación de información a un cliente autenticado (OWASP ASVS 4.0.3
/// V14.3): por eso va apagada por defecto, solo a usuarios autenticados y solo
/// con números agregados de la propia petición.
/// </para>
/// </summary>
public sealed class ServerTimingCosteCargaMiddleware(RequestDelegate siguiente, IOptions<OpcionesServerTiming> opciones)
{
    public Task InvokeAsync(HttpContext contexto)
    {
        if (opciones.Value.Habilitado)
            contexto.Response.OnStarting(EscribirCabecera, contexto);

        return siguiente(contexto);
    }

    private static Task EscribirCabecera(object estado)
    {
        var contexto = (HttpContext)estado;
        if (contexto.User.Identity?.IsAuthenticated != true)
            return Task.CompletedTask;

        if (!EsHtml(contexto.Response.ContentType))
            return Task.CompletedTask;

        // Una medición no puede tumbar la respuesta: si el scope ya no resuelve el acumulador
        // (o cualquier otra cosa falla aquí), la página sale sin cabecera.
        try
        {
            if (contexto.RequestServices?.GetService<CosteCarga>() is { } coste)
                contexto.Response.Headers.Append(CabeceraServerTiming.Nombre, CabeceraServerTiming.Formatear(coste.Leer()));
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
        }

        return Task.CompletedTask;
    }

    private static bool EsHtml(string? tipo) =>
        tipo is not null && tipo.StartsWith("text/html", StringComparison.OrdinalIgnoreCase);
}

public static class ServerTimingCosteCargaExtensions
{
    public static IApplicationBuilder UseServerTimingCosteCarga(this IApplicationBuilder app) =>
        app.UseMiddleware<ServerTimingCosteCargaMiddleware>();
}
