using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.RateLimiting;
using CaeManager.Application.Common;
using CaeManager.Application.Contacto;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;

namespace CaeManager.Web.Features.Contacto;

/// <summary>
/// Endpoint público, sin sesión, que recibe el formulario «Pedir una demo» de
/// la web estática talveg.es y lo reenvía por correo a info@talveg.es. No
/// pertenece a ningún Tenant ni toca la base de datos: los datos personales
/// solo viajan en el correo y no se guardan, así que no hay nada que retener
/// ni purgar (ASVS 4.0.3 V8.3.8).
///
/// <para>
/// Controles, con el requisito ASVS 4.0.3 que cumple cada uno:
/// <list type="bullet">
/// <item>CORS con lista cerrada de orígenes exactos, sin <c>null</c> ni credenciales (V14.5.3), y además
/// el servidor rechaza con 403 cualquier <c>Origin</c> fuera de esa lista: CORS solo lo aplica el
/// navegador.</item>
/// <item>Solo <c>POST</c> con <c>Content-Type: application/json</c>; lo demás, 415 (V13.2.1, V13.2.5).</item>
/// <item>Cuerpo acotado a <see cref="MaximoBytes"/> mientras se lee, no por la cabecera
/// <c>Content-Length</c> que declara el remitente (V11.1.4).</item>
/// <item>JSON estricto: sin propiedades desconocidas ni duplicadas, profundidad acotada (V5.1.1, V5.1.4).</item>
/// <item>Validación de longitud, patrón y lista de permitidos en servidor (V5.1.3, V5.1.4), ver
/// <see cref="ValidadorSolicitudDemo"/>.</item>
/// <item>Límite de frecuencia por IP (IPv6 agrupada por /64) y un cupo global por hora, que acota el
/// correo que puede salir aunque el ataque venga de muchas IP (V11.1.4).</item>
/// <item>Campo trampa antispam (V11.1.4): con valor se descarta sin enviar y con la misma respuesta.</item>
/// <item>Sin CSRF que defender: no lee cookies ni sesión y no actúa con la autoridad de nadie
/// (V4.2.2, V13.2.3 no aplican); exigir JSON obliga además al navegador a hacer preflight.</item>
/// <item>Correo sin inyección: asunto fijo, destinatario fijo, valores codificados para HTML y el
/// correo de quien escribe validado como dirección exacta antes de ir a <c>Reply-To</c>
/// (V5.2.3, V5.3.1).</item>
/// <item>Respuestas JSON genéricas con un código corto, sin trazas ni mensajes internos, y sin pasar
/// por las páginas de estado HTML de la aplicación (V7.4.1); <c>Cache-Control: no-store</c> (V8.2.1).</item>
/// <item>Registro sin datos personales: ni nombre, ni correo, ni teléfono, ni texto libre (V7.1.2).</item>
/// </list>
/// </para>
///
/// <para>
/// El envío pasa por la guarda de correo (<c>EnvioCorreo__Modo</c>, C-04), cerrada por defecto en
/// todo entorno: en staging y en local el endpoint contesta 503 sin que salga nada. En producción
/// sale solo con la guarda abierta, o con <c>SoloPermitidos</c> si la lista incluye info@talveg.es.
/// </para>
/// </summary>
public static class SolicitudDemoEndpoints
{
    public const string Ruta = "/api/publico/solicitud-demo";

    public const string PoliticaCors = "WebPublicaTalveg";

    public const string PoliticaLimite = "SolicitudDemoPorIp";

    public const string Destinatario = "info@talveg.es";

    /// <summary>Un formulario lleno al máximo de todos los campos ronda 1,5 KB en UTF-8; el resto es margen.</summary>
    public const int MaximoBytes = 8 * 1024;

    public static IReadOnlyList<string> OrigenesPermitidos { get; } = ["https://talveg.es", "https://www.talveg.es"];

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        MaxDepth = 4,
    };

    /// <summary>
    /// CORS, límite por IP y cupo global. Los techos se leen de
    /// <c>RateLimiting:SolicitudDemo:*</c> con valores por defecto, mismo patrón
    /// que <c>RateLimiting:Cuenta:*</c>.
    /// </summary>
    public static IServiceCollection AddSolicitudDemo(this IServiceCollection services, IConfiguration configuration)
    {
        var limitePorIp = Math.Max(1, configuration.GetValue("RateLimiting:SolicitudDemo:LimitePorIp", 5));
        var ventanaPorIpMinutos = Math.Max(1, configuration.GetValue("RateLimiting:SolicitudDemo:VentanaMinutos", 15));
        var limiteGlobalPorHora = Math.Max(1, configuration.GetValue("RateLimiting:SolicitudDemo:LimiteGlobalPorHora", 30));

        services.AddCors(opciones => opciones.AddPolicy(PoliticaCors, politica => politica
            .WithOrigins([.. OrigenesPermitidos])
            .WithMethods(HttpMethods.Post)
            .WithHeaders(HeaderNames.ContentType)
            .SetPreflightMaxAge(TimeSpan.FromHours(1))));

        services.AddRateLimiter(opciones => opciones.AddPolicy(PoliticaLimite, new LimitePorIpSolicitudDemo(
            limitePorIp, TimeSpan.FromMinutes(ventanaPorIpMinutos))));

        services.AddSingleton(new CupoGlobalSolicitudDemo(limiteGlobalPorHora));
        return services;
    }

    public static IEndpointRouteBuilder MapSolicitudDemoEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Ruta, RecibirAsync)
            .AllowAnonymous()
            .RequireCors(PoliticaCors)
            .RequireRateLimiting(PoliticaLimite)
            // Lee el cuerpo crudo, sin enlace de formulario: no hay nada que el
            // antiforgery de Blazor deba validar aquí.
            .DisableAntiforgery();

        return endpoints;
    }

    /// <summary>Pública solo para poder probarla directamente (sin InternalsVisibleTo en CaeManager.Web).</summary>
    public static async Task<IResult> RecibirAsync(
        HttpContext contexto,
        IEmailService emailService,
        CupoGlobalSolicitudDemo cupoGlobal,
        ILogger<SolicitudDemoPeticion> logger,
        CancellationToken cancellationToken)
    {
        PrepararRespuestaApi(contexto);
        var request = contexto.Request;

        var origen = request.Headers.Origin.ToString();
        if (!OrigenesPermitidos.Contains(origen, StringComparer.Ordinal))
            return Error(StatusCodes.Status403Forbidden, "origen-no-permitido");

        if (!EsJson(request.ContentType))
            return Error(StatusCodes.Status415UnsupportedMediaType, "tipo-no-soportado");

        var cuerpo = await LeerCuerpoAcotadoAsync(request, cancellationToken);
        if (cuerpo is null)
            return Error(StatusCodes.Status413PayloadTooLarge, "demasiado-grande");

        SolicitudDemoPeticion? peticion;
        try
        {
            peticion = JsonSerializer.Deserialize<SolicitudDemoPeticion>(cuerpo, OpcionesJson);
        }
        catch (JsonException)
        {
            peticion = null;
        }

        if (peticion is null)
            return Error(StatusCodes.Status400BadRequest, "json-no-valido");

        if (!string.IsNullOrEmpty(peticion.SitioWeb))
        {
            logger.LogInformation("Solicitud de demo descartada por el campo trampa.");
            return Recibida();
        }

        var (solicitud, camposNoValidos) = ValidadorSolicitudDemo.Validar(peticion);
        if (solicitud is null)
        {
            return Results.Json(
                new { error = "datos-no-validos", campos = camposNoValidos },
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Después de validar: lo que no pasa no gasta cupo de correo, solo el de su IP.
        using var permiso = cupoGlobal.Intentar();
        if (!permiso.IsAcquired)
        {
            logger.LogWarning("Solicitud de demo rechazada: cupo global de envíos agotado.");
            return Error(StatusCodes.Status429TooManyRequests, "demasiadas-solicitudes");
        }

        var resultado = await emailService.EnviarAsync(
            Destinatario,
            CorreoSolicitudDemo.Asunto(solicitud),
            CorreoSolicitudDemo.CuerpoHtml(solicitud),
            TipoAvisoCorreo.Transaccional,
            responderA: solicitud.Correo,
            cancellationToken);

        if (resultado.EsFallido)
        {
            logger.LogWarning("Solicitud de demo no enviada: {Codigo}.", resultado.Error.Codigo);
            return Error(StatusCodes.Status503ServiceUnavailable, "no-disponible");
        }

        logger.LogInformation("Solicitud de demo enviada (perfil {Perfil}).", solicitud.Perfil);
        return Recibida();
    }

    private static IResult Recibida() =>
        Results.Json(new { estado = "recibida" }, statusCode: StatusCodes.Status202Accepted);

    private static IResult Error(int codigoHttp, string codigo) =>
        Results.Json(new { error = codigo }, statusCode: codigoHttp);

    /// <summary>
    /// <c>UseStatusCodePagesWithReExecute</c> reescribe como página HTML de la
    /// aplicación todo 4xx/5xx que salga sin cuerpo. Hoy cada respuesta de aquí
    /// lleva su cuerpo JSON (también el 429 del limitador, en
    /// <c>OnRejected</c>), que es lo que de verdad lo evita; desactivarlo es la
    /// segunda barrera, para que una respuesta futura sin cuerpo no salga en HTML.
    /// </summary>
    private static void PrepararRespuestaApi(HttpContext contexto)
    {
        var paginasEstado = contexto.Features.Get<IStatusCodePagesFeature>();
        if (paginasEstado is not null)
            paginasEstado.Enabled = false;

        contexto.Response.Headers.CacheControl = "no-store";
    }

    private static bool EsJson(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var tipo)
        && tipo.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
        && (tipo.Charset.Length == 0 || tipo.Charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <c>Content-Length</c> lo declara el remitente: el tope real se aplica
    /// mientras se lee. Devuelve null si se pasa — el llamador contesta 413.
    /// </summary>
    private static async Task<byte[]?> LeerCuerpoAcotadoAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength is > MaximoBytes)
            return null;

        using var destino = new MemoryStream();
        var lote = new byte[2048];
        int leidos;
        while ((leidos = await request.Body.ReadAsync(lote, cancellationToken)) > 0)
        {
            if (destino.Length + leidos > MaximoBytes)
                return null;

            destino.Write(lote, 0, leidos);
        }

        return destino.ToArray();
    }

    /// <summary>
    /// Clave de partición del límite por IP. Una IPv6 se agrupa por su /64: es
    /// lo que suele recibir un solo cliente, y por dirección completa bastaría
    /// con rotar la parte baja para no topar nunca. La IP ya llega resuelta por
    /// <c>UseForwardedHeaders</c> (detrás de Caddy, la que este añade a
    /// <c>X-Forwarded-For</c>).
    /// </summary>
    public static string ClaveIp(IPAddress? ip)
    {
        if (ip is null)
            return "desconocida";

        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (ip.AddressFamily != AddressFamily.InterNetworkV6)
            return ip.ToString();

        var bytes = ip.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    private sealed class LimitePorIpSolicitudDemo(int limite, TimeSpan ventana) : IRateLimiterPolicy<string>
    {
        public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected { get; } = async (contexto, cancellationToken) =>
        {
            var http = contexto.HttpContext;
            PrepararRespuestaApi(http);
            if (contexto.Lease.TryGetMetadata(MetadataName.RetryAfter, out var reintento))
                http.Response.Headers.RetryAfter = ((int)Math.Ceiling(reintento.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);

            http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await http.Response.WriteAsJsonAsync(new { error = "demasiadas-solicitudes" }, cancellationToken);
        };

        public RateLimitPartition<string> GetPartition(HttpContext httpContext) =>
            RateLimitPartition.GetFixedWindowLimiter(
                ClaveIp(httpContext.Connection.RemoteIpAddress),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limite,
                    Window = ventana,
                    QueueLimit = 0,
                });
    }
}

/// <summary>
/// Techo de correos de solicitud de demo por hora para todo el proceso, venga
/// de la IP que venga (ASVS 4.0.3 V11.1.4). En memoria: suficiente con una
/// réplica, igual que el resto de limitadores de la aplicación.
/// </summary>
public sealed class CupoGlobalSolicitudDemo(int limitePorHora) : IDisposable
{
    private readonly FixedWindowRateLimiter limitador = new(new FixedWindowRateLimiterOptions
    {
        PermitLimit = limitePorHora,
        Window = TimeSpan.FromHours(1),
        QueueLimit = 0,
    });

    public RateLimitLease Intentar() => limitador.AttemptAcquire();

    public void Dispose() => limitador.Dispose();
}
