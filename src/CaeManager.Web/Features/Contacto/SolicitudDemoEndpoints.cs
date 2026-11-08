using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.RateLimiting;
using CaeManager.Application.Common;
using CaeManager.Application.Contacto;
using CaeManager.Domain.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;

namespace CaeManager.Web.Features.Contacto;

/// <summary>
/// Endpoint público, sin sesión, que recibe el formulario «Pedir una demo» de
/// la web estática talveg.es y lo reenvía por correo a info@talveg.es. No
/// pertenece a ningún Tenant ni toca la base de datos de la aplicación. Los
/// datos personales sí quedan en el buzón info@talveg.es (y en sus copias): su
/// retención la fija la política de ese buzón, que esta clase no controla, así
/// que no se atribuye aquí el cumplimiento de ASVS 4.0.3 V8.3.8.
///
/// <para>
/// Controles, con el requisito ASVS 4.0.3 que cumple cada uno:
/// <list type="bullet">
/// <item>CORS con lista cerrada de orígenes exactos, sin <c>null</c> ni credenciales (V14.5.3), y además
/// el servidor rechaza con 403 cualquier <c>Origin</c> fuera de esa lista: CORS solo lo aplica el
/// navegador.</item>
/// <item>Solo <c>POST</c> con <c>Content-Type: application/json</c>; otro tipo, 415, y otro método, 405
/// en JSON (V13.2.1, V13.2.5).</item>
/// <item>Cuerpo acotado a <see cref="MaximoBytes"/> mientras se lee (con un tope de respaldo en Kestrel), no por la
/// cabecera <c>Content-Length</c> que declara el remitente (V11.1.4).</item>
/// <item>JSON estricto: sin propiedades desconocidas ni duplicadas, profundidad acotada (V5.1.1, V5.1.4).</item>
/// <item>Validación de longitud, patrón y lista de permitidos en servidor (V5.1.3, V5.1.4), ver
/// <see cref="ValidadorSolicitudDemo"/>.</item>
/// <item>Límite de frecuencia por IP (IPv6 agrupada por /64) que solo cuenta peticiones con un
/// <c>Origin</c> permitido, para que otra página no pueda gastar la cuota de sus visitantes; y un cupo
/// global por hora que acota el correo que llega a info@talveg.es aunque el envío venga de muchas IP
/// (V11.1.4).</item>
/// <item>Campo trampa antispam (V11.1.4): con valor se descarta sin enviar y con la misma respuesta.</item>
/// <item>Sin CSRF que defender: no lee cookies ni sesión y no actúa con la autoridad de nadie
/// (V4.2.2, V13.2.3 no aplican); exigir JSON obliga además al navegador a hacer preflight.</item>
/// <item>Correo sin inyección: asunto fijo, destinatario fijo, valores codificados para HTML y el
/// correo de quien escribe validado como dirección ASCII exacta antes de ir a <c>Reply-To</c>
/// (V5.2.3, V5.3.1).</item>
/// <item>Respuestas JSON genéricas con un código corto, sin trazas ni mensajes internos, también ante
/// una excepción del envío o de la lectura, y sin pasar por las páginas HTML de estado o de error de la
/// aplicación (V7.4.1); <c>Cache-Control: no-store</c> (V8.2.1).</item>
/// <item>Registro sin datos personales: ni nombre, ni correo, ni teléfono, ni texto libre; de una
/// excepción solo se registra el tipo (V7.1.2).</item>
/// </list>
/// </para>
///
/// <para>
/// El envío pasa por la guarda de correo (<c>EnvioCorreo__Modo</c>, C-04), cerrada por defecto en
/// todo entorno: en staging y en local el endpoint contesta 503 sin que salga nada. En producción
/// sale solo con la guarda abierta, o con <c>SoloPermitidos</c> si la lista incluye info@talveg.es.
/// </para>
///
/// <para>
/// <b>Riesgo asumido</b>: quien mande formularios válidos desde muchas IP agota el cupo global y deja
/// el formulario en 503 <c>cupo-agotado</c> hasta que la ventana corre. Es el precio de acotar el
/// correo; el agotamiento se registra como error para que avise, y el formulario debe ofrecer
/// escribir a info@talveg.es ante cualquier 429 o 503. Frenar el envío distribuido de verdad pide un
/// desafío antirrobot de terceros, que es una decisión de producto aparte.
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
        var limiteGlobalPorHora = Math.Max(1, configuration.GetValue("RateLimiting:SolicitudDemo:LimiteGlobalPorHora", 60));

        services.AddCors(opciones => opciones.AddPolicy(PoliticaCors, politica => politica
            .WithOrigins([.. OrigenesPermitidos])
            .WithMethods(HttpMethods.Post)
            .WithHeaders(HeaderNames.ContentType)
            .SetPreflightMaxAge(TimeSpan.FromHours(1))));

        services.AddRateLimiter(opciones => opciones.AddPolicy(PoliticaLimite, new LimitePorIpSolicitudDemo(
            limitePorIp, TimeSpan.FromMinutes(ventanaPorIpMinutos))));

        services.AddSingleton(new CupoGlobalSolicitudDemo(limiteGlobalPorHora, TimeProvider.System));
        return services;
    }

    public static IEndpointRouteBuilder MapSolicitudDemoEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var ruta = endpoints.MapGroup(Ruta).AllowAnonymous();

        ruta.MapPost(string.Empty, RecibirAsync)
            .RequireCors(PoliticaCors)
            .RequireRateLimiting(PoliticaLimite)
            // Lee el cuerpo crudo, sin enlace de formulario: no hay nada que el
            // antiforgery de Blazor deba validar aquí.
            .DisableAntiforgery();

        // Sin esto, otro método recibe el 405 vacío del enrutado y las páginas de
        // estado lo convierten en la página HTML de la aplicación. OPTIONS no
        // está: el preflight lo contesta UseCors antes de llegar aquí.
        ruta.MapMethods(
            string.Empty,
            [HttpMethods.Get, HttpMethods.Head, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete],
            (HttpContext contexto) =>
            {
                PrepararRespuestaApi(contexto);
                contexto.Response.Headers.Allow = HttpMethods.Post;
                return Error(StatusCodes.Status405MethodNotAllowed, "metodo-no-permitido");
            });

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

        if (!OrigenPermitido(contexto))
            return Error(StatusCodes.Status403Forbidden, "origen-no-permitido");

        if (!EsJson(request.ContentType))
            return Error(StatusCodes.Status415UnsupportedMediaType, "tipo-no-soportado");

        byte[]? cuerpo;
        try
        {
            cuerpo = await LeerCuerpoAcotadoAsync(contexto, cancellationToken);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            cuerpo = null;
        }
        catch (Exception ex) when (ex is BadHttpRequestException or IOException)
        {
            return Error(StatusCodes.Status400BadRequest, "peticion-no-valida");
        }

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

        if (!string.IsNullOrEmpty(peticion.CampoTrampa))
        {
            // Aviso y no información: si un autocompletado empezara a rellenarlo,
            // se perderían solicitudes reales sin que nadie lo viera.
            logger.LogWarning("Solicitud de demo descartada por el campo trampa.");
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
        if (!cupoGlobal.IntentarReservar(out var reserva, out var reintentarEn))
        {
            logger.LogError("Solicitud de demo rechazada: cupo global de envíos agotado.");
            contexto.Response.Headers.RetryAfter = ((int)Math.Ceiling(reintentarEn.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return Error(StatusCodes.Status503ServiceUnavailable, "cupo-agotado");
        }

        Result resultado;
        try
        {
            resultado = await emailService.EnviarAsync(
                Destinatario,
                CorreoSolicitudDemo.Asunto(solicitud),
                CorreoSolicitudDemo.CuerpoHtml(solicitud),
                TipoAvisoCorreo.Transaccional,
                responderA: solicitud.Correo,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Solo el tipo: el mensaje de una excepción de correo puede llevar direcciones.
            cupoGlobal.Liberar(reserva);
            logger.LogError("Solicitud de demo no enviada: excepción {Tipo} en el envío.", ex.GetType().FullName);
            return Error(StatusCodes.Status503ServiceUnavailable, "no-disponible");
        }

        if (resultado.EsFallido)
        {
            cupoGlobal.Liberar(reserva);
            logger.LogWarning("Solicitud de demo no enviada: {Codigo}.", resultado.Error.Codigo);
            return Error(StatusCodes.Status503ServiceUnavailable, "no-disponible");
        }

        logger.LogInformation("Solicitud de demo enviada (perfil {Perfil}).", solicitud.Perfil);
        return Recibida();
    }

    /// <summary>
    /// Clave del límite por IP, o null si la petición no trae un <c>Origin</c>
    /// permitido: esa ya la rechaza el endpoint con 403 sin enviar nada, y
    /// contarla dejaría a cualquier página gastar con <c>fetch</c> la cuota de
    /// la IP de quien la visita.
    /// </summary>
    public static string? ClaveDeLimite(HttpContext contexto) =>
        OrigenPermitido(contexto) ? ClaveIp(contexto.Connection.RemoteIpAddress) : null;

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

    private static bool OrigenPermitido(HttpContext contexto) =>
        OrigenesPermitidos.Contains(contexto.Request.Headers.Origin.ToString(), StringComparer.Ordinal);

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
    /// <c>Content-Length</c> lo declara el remitente: el tope exacto lo pone este
    /// bucle mientras lee, y Kestrel (<see cref="IHttpMaxRequestBodySizeFeature"/>)
    /// uno de respaldo algo mayor. Devuelve null si se pasa — el llamador contesta 413.
    /// </summary>
    private static async Task<byte[]?> LeerCuerpoAcotadoAsync(HttpContext contexto, CancellationToken cancellationToken)
    {
        var request = contexto.Request;
        if (request.ContentLength is > MaximoBytes)
            return null;

        // Respaldo, no el borde: Kestrel corta un cuerpo chunked de exactamente
        // MaximoBytes (medido en SolicitudDemoEndpointsTests), así que lleva un
        // margen. Lo que evita es que Kestrel drene megas de un cuerpo ya rechazado.
        var tope = contexto.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (tope is { IsReadOnly: false })
            tope.MaxRequestBodySize = MaximoBytes + 1024;

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

    private sealed class LimitePorIpSolicitudDemo(int limite, TimeSpan ventana) : IRateLimiterPolicy<string>
    {
        public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected { get; } = async (contexto, cancellationToken) =>
        {
            var http = contexto.HttpContext;
            PrepararRespuestaApi(http);
            if (contexto.Lease.TryGetMetadata(MetadataName.RetryAfter, out var reintento))
                http.Response.Headers.RetryAfter = ((int)Math.Ceiling(reintento.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

            http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await http.Response.WriteAsJsonAsync(new { error = "demasiadas-solicitudes" }, cancellationToken);
        };

        public RateLimitPartition<string> GetPartition(HttpContext httpContext) =>
            ClaveDeLimite(httpContext) is { } clave
                ? RateLimitPartition.GetFixedWindowLimiter(
                    clave,
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = limite,
                        Window = ventana,
                        QueueLimit = 0,
                    })
                : RateLimitPartition.GetNoLimiter("origen-no-permitido");
    }
}

/// <summary>
/// Techo de correos de solicitud de demo por hora para todo el proceso, venga
/// de la IP que venga (ASVS 4.0.3 V11.1.4). Ventana deslizante de una hora que
/// solo cuenta los correos que salen: un envío que falla devuelve su plaza, así
/// que una caída del SMTP no deja el formulario cerrado al volver. En memoria:
/// suficiente con una réplica, igual que el resto de limitadores de la
/// aplicación.
/// </summary>
public sealed class CupoGlobalSolicitudDemo(int limitePorHora, TimeProvider reloj)
{
    private static readonly TimeSpan Ventana = TimeSpan.FromHours(1);

    private readonly List<DateTimeOffset> reservas = [];

    private readonly Lock cerrojo = new();

    /// <summary>Reserva una plaza; si no queda ninguna, dice cuándo se libera la primera.</summary>
    public bool IntentarReservar(out DateTimeOffset reserva, out TimeSpan reintentarEn)
    {
        lock (cerrojo)
        {
            var ahora = reloj.GetUtcNow();
            reservas.RemoveAll(r => ahora - r >= Ventana);

            if (reservas.Count >= limitePorHora)
            {
                reserva = default;
                reintentarEn = reservas.Min() + Ventana - ahora;
                return false;
            }

            reserva = ahora;
            reintentarEn = TimeSpan.Zero;
            reservas.Add(ahora);
            return true;
        }
    }

    /// <summary>Devuelve la plaza de un envío que no salió.</summary>
    public void Liberar(DateTimeOffset reserva)
    {
        lock (cerrojo)
            reservas.Remove(reserva);
    }
}
