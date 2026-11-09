using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CaeManager.Application.Integraciones;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Integraciones;

/// <summary>
/// Adaptador concreto de Microsoft 365 (ver Project-Hydra-Negocio/tecnico/ARQUITECTURA-INTEGRACIONES.md §
/// 5/12) — flujo delegado (authorization code + offline_access), no
/// client-credentials como <c>GraphEmailService</c>: aquí no hay "el propio
/// buzón de Hydra", cada <c>ConexionIntegracion</c> es el buzón real de un
/// cliente distinto, consentido por su propio administrador. Las llamadas
/// sobre el buzón propio de quien consintió usan <c>/me/...</c> (resuelto por
/// el token, no por un UPN en la URL); las de un buzón compartido usan
/// <c>/users/{dirección}/...</c> con el token de aplicación del directorio
/// (<see cref="ObtenerTokenAplicacionAsync"/>; ver <see cref="RutaBuzon"/>).
/// </summary>
public class Microsoft365GraphClient(
    HttpClient httpClient,
    IOptions<Microsoft365GraphOptions> opciones,
    ILogger<Microsoft365GraphClient> logger) : IMicrosoft365GraphClient
{
    private const string EndpointAutorizacion = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize";
    private const string EndpointToken = "https://login.microsoftonline.com/common/oauth2/v2.0/token";
    // Solo el buzón propio de quien consiente. Los buzones compartidos no van por aquí: usan el
    // token de aplicación del directorio (ObtenerTokenAplicacionAsync), acotado en Exchange.
    internal const string ScopesDelegados =
        "offline_access https://graph.microsoft.com/Mail.Read https://graph.microsoft.com/Mail.Send";

    private const string ScopeAplicacion = "https://graph.microsoft.com/.default";

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TokenAplicacionGraphDto> TokensAplicacion = new();

    private const string RaizGraph = "https://graph.microsoft.com/v1.0/";

    // Máximo permitido por Graph para suscripciones sobre mensajes de
    // correo (~2.94 días) — ver RenovacionSuscripcionWebhookHostedService,
    // que renueva bastante antes de que esto expire.
    private static readonly TimeSpan DuracionMaximaSuscripcion = TimeSpan.FromMinutes(4230);

    public string ConstruirUrlAutorizacion(string redirectUri, string state)
    {
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = opciones.Value.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["response_mode"] = "query",
            ["scope"] = ScopesDelegados,
            ["state"] = state,
        };

        return $"{EndpointAutorizacion}?{string.Join('&', query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value ?? string.Empty)}"))}";
    }

    public async Task<Result<TokensGraphDto>> IntercambiarCodigoPorTokensAsync(
        string code, string redirectUri, CancellationToken cancellationToken)
    {
        var contenido = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["scope"] = ScopesDelegados,
        };

        var credencial = AgregarCredencialDeCliente(contenido, EndpointToken);
        if (credencial.EsFallido)
            return Result.Fallo<TokensGraphDto>(credencial.Error);

        return await PedirTokensAsync(contenido, cancellationToken);
    }

    public async Task<Result<TokensGraphDto>> RefrescarTokensAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var contenido = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = ScopesDelegados,
        };

        var credencial = AgregarCredencialDeCliente(contenido, EndpointToken);
        if (credencial.EsFallido)
            return Result.Fallo<TokensGraphDto>(credencial.Error);

        return await PedirTokensAsync(contenido, cancellationToken);
    }

    public async Task<Result<TokenAplicacionGraphDto>> ObtenerTokenAplicacionAsync(string directorioId, CancellationToken cancellationToken)
    {
        // El identificador va en la ruta del endpoint: solo un GUID, para que no pueda apuntar a otro
        // sitio ni a /common u /organizations (que no admiten client credentials con sentido aquí).
        if (!Guid.TryParse(directorioId, out var directorio) || directorio == Guid.Empty)
            return Result.Fallo<TokenAplicacionGraphDto>(Error.Crear(
                "Integraciones.Microsoft365.DirectorioInvalido", "El directorio de Microsoft no es válido."));

        // Un token por directorio para todos sus buzones compartidos: sin esta caché cada lectura, envío o
        // notificación pediría uno nuevo a Entra. Se descarta cinco minutos antes de caducar.
        var claveCache = $"{opciones.Value.ClientId}|{directorio:D}";
        if (TokensAplicacion.TryGetValue(claveCache, out var cacheado) && cacheado.ExpiraUtc > DateTime.UtcNow.AddMinutes(5))
            return Result.Exito(cacheado);

        var endpoint = $"https://login.microsoftonline.com/{directorio:D}/oauth2/v2.0/token";
        var contenido = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = ScopeAplicacion,
        };

        var credencial = AgregarCredencialDeCliente(contenido, endpoint);
        if (credencial.EsFallido)
            return Result.Fallo<TokenAplicacionGraphDto>(credencial.Error);

        using var peticion = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new FormUrlEncodedContent(contenido) };
        try
        {
            using var respuesta = await httpClient.SendAsync(peticion, cancellationToken);
            if (!respuesta.IsSuccessStatusCode)
            {
                var cuerpoError = await respuesta.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError(
                    "Microsoft Entra ID devolvió {StatusCode} al pedir un token de aplicación: {CodigoError}",
                    (int)respuesta.StatusCode, CodigoErrorGraph.Extraer(cuerpoError));
                return Result.Fallo<TokenAplicacionGraphDto>(EsRespuestaTransitoria(respuesta.StatusCode)
                    ? ConEsperaPedida(ErrorTransitorio, respuesta)
                    : Error.Crear("Integraciones.Microsoft365.ErrorAutenticacion", "No pudimos autenticar con Microsoft."));
            }

            // Un 200 con JSON válido pero sin el token no es un rechazo de Entra (que respondería 4xx): es una respuesta
            // incompleta, como una ilegible, y se reintenta sin pasar la conexión a ConError.
            var cuerpo = await respuesta.Content.ReadFromJsonAsync<RespuestaTokenGraph>(cancellationToken);
            if (string.IsNullOrWhiteSpace(cuerpo?.AccessToken))
            {
                logger.LogError("Microsoft Entra ID devolvió un 200 sin access_token al pedir un token de aplicación.");
                return Result.Fallo<TokenAplicacionGraphDto>(ErrorTransitorio);
            }

            // La concesión de Entra y la de Exchange se suman: con un permiso de aplicación de buzón en Entra el
            // token abre todos los buzones del directorio y el ámbito de RBAC for Applications deja de acotar
            // nada. Esos permisos viajan en el claim «roles»; los de Exchange no. Este flujo no necesita ninguno
            // de Entra, así que cualquiera hace fallar cerrado.
            if (TokenAplicacion.TienePermisosDeEntraOEsIlegible(cuerpo.AccessToken))
            {
                logger.LogError(
                    "El token de aplicación del directorio {Directorio} trae permisos de aplicación concedidos en Entra: no se usa.", directorio);
                return Result.Fallo<TokenAplicacionGraphDto>(Error.Crear(
                    "Integraciones.Microsoft365.PermisoSinAcotar",
                    "La aplicación tiene en ese directorio permisos de aplicación concedidos en Entra que no acota Exchange: el administrador debe retirarlos."));
            }

            var token = new TokenAplicacionGraphDto(cuerpo.AccessToken, DateTime.UtcNow.AddSeconds(cuerpo.ExpiresIn ?? 3600));
            TokensAplicacion[claveCache] = token;
            return Result.Exito(token);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al pedir un token de aplicación a Microsoft Entra ID.");
            return Result.Fallo<TokenAplicacionGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorRed", "No pudimos conectar con Microsoft."));
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Microsoft Entra ID no respondió a tiempo al pedir un token de aplicación.");
            return Result.Fallo<TokenAplicacionGraphDto>(ErrorTiempoAgotado);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            // Un 200 ilegible no es un rechazo de Entra: se reintenta.
            logger.LogError(ex, "Microsoft Entra ID devolvió una respuesta ilegible al pedir un token de aplicación.");
            return Result.Fallo<TokenAplicacionGraphDto>(ErrorTransitorio);
        }
    }

    /// <summary>
    /// Añade al cuerpo del canje la credencial con la que el App Registration se
    /// autentica: un aserto firmado con certificado (P44b) si hay certificado
    /// configurado, o el <c>client_secret</c> de siempre si no. Nunca las dos a la
    /// vez: con certificado, el secreto no se envía aunque también esté configurado.
    /// Se genera un aserto NUEVO en cada llamada (<c>jti</c> único): Entra rechaza la
    /// reutilización de un mismo aserto.
    /// </summary>
    private Result AgregarCredencialDeCliente(Dictionary<string, string> contenido, string endpointToken)
    {
        var config = opciones.Value;
        contenido["client_id"] = config.ClientId!;

        // Migración a medias: no se cae en silencio al secreto aunque alguien invoque
        // el cliente sin pasar por EstaConfigurado.
        if (config.CertificadoIncompleto)
        {
            logger.LogError("Certificado de cliente de Microsoft 365 incompleto: hay que informar CertificadoRuta y ClavePrivadaRuta a la vez.");
            return Result.Fallo(Error.Crear(
                "Integraciones.Microsoft365.ErrorAutenticacion", "No pudimos autenticar con Microsoft."));
        }

        if (!config.UsaCertificado)
        {
            contenido["client_secret"] = config.ClientSecret!;
            return Result.Exito();
        }

        var aserto = AsertoClienteMicrosoft365.Crear(
            config.ClientId!, endpointToken, config.CertificadoRuta!, config.ClavePrivadaRuta!, DateTimeOffset.UtcNow);
        if (aserto.EsFallido)
        {
            logger.LogError("No se pudo generar el aserto de cliente de Microsoft 365 con el certificado configurado: {Codigo}.", aserto.Error.Codigo);
            return Result.Fallo(Error.Crear(
                "Integraciones.Microsoft365.ErrorAutenticacion", "No pudimos autenticar con Microsoft."));
        }

        contenido["client_assertion_type"] = AsertoClienteMicrosoft365.TipoAserto;
        contenido["client_assertion"] = aserto.Valor;
        return Result.Exito();
    }

    private async Task<Result<TokensGraphDto>> PedirTokensAsync(
        Dictionary<string, string> contenido, CancellationToken cancellationToken)
    {
        using var peticion = new HttpRequestMessage(HttpMethod.Post, EndpointToken) { Content = new FormUrlEncodedContent(contenido) };

        try
        {
            using var respuesta = await httpClient.SendAsync(peticion, cancellationToken);
            if (!respuesta.IsSuccessStatusCode)
            {
                var cuerpoError = await respuesta.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError("Microsoft Entra ID devolvió {StatusCode} al canjear tokens: {CodigoError}", (int)respuesta.StatusCode, CodigoErrorGraph.Extraer(cuerpoError));
                return Result.Fallo<TokensGraphDto>(EsRespuestaTransitoria(respuesta.StatusCode)
                    ? ConEsperaPedida(ErrorTransitorio, respuesta)
                    : Error.Crear("Integraciones.Microsoft365.ErrorAutenticacion", "No pudimos autenticar con Microsoft."));
            }

            // Un 200 con JSON válido pero sin alguno de los dos tokens no es un rechazo de Entra (que respondería 4xx):
            // es una respuesta incompleta, como una ilegible, y se reintenta sin pasar la conexión a ConError.
            var cuerpo = await respuesta.Content.ReadFromJsonAsync<RespuestaTokenGraph>(cancellationToken);
            if (string.IsNullOrWhiteSpace(cuerpo?.AccessToken) || string.IsNullOrWhiteSpace(cuerpo.RefreshToken))
            {
                logger.LogError("Microsoft Entra ID devolvió un 200 sin access_token o sin refresh_token al canjear tokens.");
                return Result.Fallo<TokensGraphDto>(ErrorTransitorio);
            }

            // expires_in casi siempre viaja (Entra ID lo manda de serie), pero
            // ante su ausencia se asume el mínimo típico de Graph (1 h) en vez
            // de fallar el canje por un campo que no es indispensable.
            var expiraUtc = DateTime.UtcNow.AddSeconds(cuerpo.ExpiresIn ?? 3600);

            // El directorio de quien inició sesión es el claim «tid» del access token que Entra devuelve en ESTA
            // respuesta del endpoint de tokens (los scopes no piden openid, así que no hay id_token). Se lee sin
            // validar la firma por el mismo motivo que TokenAplicacion: el token llega directamente de Entra por
            // TLS en respuesta a nuestra petición. Nunca sale del navegador, de «state» ni de la query del callback.
            return Result.Exito(new TokensGraphDto(
                cuerpo.AccessToken, cuerpo.RefreshToken, expiraUtc, TokenAplicacion.LeerDirectorio(cuerpo.AccessToken)));
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al canjear tokens con Microsoft Entra ID.");
            return Result.Fallo<TokensGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorRed", "No pudimos conectar con Microsoft."));
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Microsoft Entra ID no respondió a tiempo al canjear tokens.");
            return Result.Fallo<TokensGraphDto>(ErrorTiempoAgotado);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            // Un 200 con un cuerpo ilegible (JSON roto o charset inválido) no es un rechazo de Entra: se reintenta.
            logger.LogError(ex, "Microsoft Entra ID devolvió una respuesta de tokens ilegible.");
            return Result.Fallo<TokensGraphDto>(ErrorTransitorio);
        }
    }

    public async Task<Result<string>> ObtenerBuzonEmailAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var peticion = NuevaPeticionGraph(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me?$select=mail,userPrincipalName", accessToken);

        try
        {
            using var respuesta = await httpClient.SendAsync(peticion, cancellationToken);
            if (!respuesta.IsSuccessStatusCode)
                return Result.Fallo<string>(Error.Crear("Integraciones.Microsoft365.ErrorApi", "No pudimos leer el buzón conectado."));

            var perfil = await respuesta.Content.ReadFromJsonAsync<PerfilGraph>(cancellationToken);
            var buzon = perfil?.Mail ?? perfil?.UserPrincipalName;
            return string.IsNullOrWhiteSpace(buzon)
                ? Result.Fallo<string>(Error.Crear("Integraciones.Microsoft365.ErrorApi", "No pudimos leer el buzón conectado."))
                : Result.Exito(buzon);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al leer el perfil de Microsoft Graph.");
            return Result.Fallo<string>(Error.Crear("Integraciones.Microsoft365.ErrorRed", "No pudimos conectar con Microsoft."));
        }
    }

    public async Task<Result<string>> ObtenerIdentidadBuzonAsync(string accessToken, BuzonGraph buzon, CancellationToken cancellationToken)
    {
        using var peticion = NuevaPeticionGraph(
            HttpMethod.Get, $"{RutaBuzon(buzon)}/mailFolders('Inbox')?$select=id", accessToken);
        // NuevaPeticionGraph ya pide el Id inmutable (Prefer: IdType="ImmutableId"), que no cambia aunque la
        // carpeta se mueva ni depende de la dirección por la que se abra el buzón: es lo que hace de él una
        // identidad y no solo una prueba de acceso.

        try
        {
            using var respuesta = await httpClient.SendAsync(peticion, cancellationToken);
            if (respuesta.IsSuccessStatusCode)
            {
                var carpeta = await respuesta.Content.ReadFromJsonAsync<CarpetaIdGraph>(cancellationToken);
                return string.IsNullOrWhiteSpace(carpeta?.Id) || carpeta.Id.Length > LongitudMaximaIdentidadBuzon
                    ? Result.Fallo<string>(Error.Crear("Integraciones.Microsoft365.ErrorApi", "No pudimos leer el buzón conectado."))
                    : Result.Exito(carpeta.Id);
            }

            var cuerpoError = await respuesta.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning(
                "Microsoft Graph devolvió {StatusCode} al comprobar el acceso a un buzón (compartido: {EsCompartido}): {CodigoError}",
                (int)respuesta.StatusCode, buzon.EsCompartido, CodigoErrorGraph.Extraer(cuerpoError));

            // 403/404: el buzón está fuera del ámbito de RBAC for Applications que el administrador de
            // Exchange asignó a la aplicación en ese directorio, o la dirección no existe. Graph no
            // distingue los dos casos de forma fiable, así que el mensaje los nombra a la vez.
            return respuesta.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound
                ? Result.Fallo<string>(Error.Crear(
                    "Integraciones.Microsoft365.SinAccesoBuzon",
                    "No podemos abrir ese buzón: comprueba la dirección y que el administrador de Exchange haya incluido el buzón en el ámbito de la aplicación TALVEG."))
                : Result.Fallo<string>(ConEsperaPedida(
                    Error.Crear("Integraciones.Microsoft365.ErrorApi", "No pudimos leer el buzón conectado."), respuesta));
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al comprobar el acceso a un buzón vía Microsoft Graph.");
            return Result.Fallo<string>(Error.Crear("Integraciones.Microsoft365.ErrorRed", "No pudimos conectar con Microsoft."));
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Microsoft Graph no respondió a tiempo al comprobar el acceso a un buzón.");
            return Result.Fallo<string>(ErrorTiempoAgotado);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return Result.Fallo<string>(Error.Crear("Integraciones.Microsoft365.ErrorApi", "No pudimos leer el buzón conectado."));
        }
    }

    /// <summary>Tope de la identidad del buzón en la reclamación; un Id inmutable de carpeta ronda los 150 caracteres.</summary>
    internal const int LongitudMaximaIdentidadBuzon = CaeManager.Domain.Integraciones.ReclamacionBuzonIntegracion.LongitudMaximaIdentidadBuzon;

    /// <summary>
    /// Un timeout del <see cref="HttpClient"/> llega como <see cref="TaskCanceledException"/> sin que nadie haya
    /// cancelado: es un fallo recuperable de Microsoft, no una cancelación, y no debe cortar la desconexión de un
    /// buzón ni la renovación del resto del Tenant. La cancelación pedida de verdad se sigue propagando.
    /// </summary>
    private static readonly Error ErrorTiempoAgotado =
        Error.Crear(CodigosErrorMicrosoft365.TiempoAgotado, "Microsoft no respondió a tiempo.");

    /// <summary>
    /// Microsoft no rechazó la petición, solo no pudo atenderla ahora (<see cref="EsRespuestaTransitoria"/>, o un 200
    /// de tokens ilegible). Quien renueva o verifica lo reintenta en el siguiente ciclo sin pasar la conexión a ConError.
    /// </summary>
    private static readonly Error ErrorTransitorio =
        Error.Crear(CodigosErrorMicrosoft365.Transitorio, "Microsoft no ha podido atender la petición ahora; se reintentará.");

    /// <summary>
    /// Un 429 con <c>Retry-After</c> (en segundos o como fecha): el error lleva esa espera
    /// (<see cref="Error.ReintentarTras"/>) para que la verificación de la identidad no vuelva antes. Sin la cabecera, o
    /// en otro código, el error tal cual.
    /// </summary>
    internal static Error ConEsperaPedida(Error error, HttpResponseMessage respuesta)
    {
        if (respuesta.StatusCode != System.Net.HttpStatusCode.TooManyRequests || respuesta.Headers.RetryAfter is not { } reintento)
            return error;

        var espera = reintento.Delta
            ?? (reintento.Date is { } fecha ? fecha - DateTimeOffset.UtcNow : (TimeSpan?)null);
        return espera is { } valor && valor > TimeSpan.Zero ? error.ConReintentarTras(valor) : error;
    }

    /// <summary>408, 429 o 5xx: la respuesta no dice nada de la credencial ni de la suscripción.</summary>
    internal static bool EsRespuestaTransitoria(System.Net.HttpStatusCode codigo) =>
        codigo is System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests || (int)codigo >= 500;

    private sealed record CarpetaIdGraph(string? Id);

    /// <summary>
    /// Raíz de las llamadas sobre un buzón: <c>/me</c> para el propio de quien consintió (resuelto por
    /// el token, sin depender de que <c>mail</c> coincida con el UPN) y <c>/users/{dirección}</c> para
    /// uno compartido. La dirección se escapa: viene de lo que escribió quien conectó el buzón.
    /// </summary>
    internal static string RutaBuzon(BuzonGraph buzon) => RaizGraph + RecursoBuzon(buzon);

    /// <summary>La misma raíz sin el prefijo de Graph: es como la quiere el <c>resource</c> de una suscripción.</summary>
    internal static string RecursoBuzon(BuzonGraph buzon) =>
        buzon.EsCompartido ? $"users/{Uri.EscapeDataString(buzon.Direccion)}" : "me";

    public async Task<Result> EnviarRespuestaAsync(
        string accessToken, BuzonGraph buzon, string mensajeExternoIdOrigen, string cuerpoHtml,
        IReadOnlyList<AdjuntoParaEnviarDto>? adjuntos, CancellationToken cancellationToken)
    {
        // /reply preserva conversationId/threading automáticamente — nunca
        // reconstruir In-Reply-To/References a mano (ver
        // Project-Hydra-Negocio/tecnico/ARQUITECTURA-INTEGRACIONES.md § 12.2). Los adjuntos van dentro de
        // "message" — es la forma en que Graph permite adjuntar algo más al
        // responder, en vez de solo el comentario de texto.
        using var peticion = NuevaPeticionGraph(
            HttpMethod.Post, $"{RutaBuzon(buzon)}/messages/{Uri.EscapeDataString(mensajeExternoIdOrigen)}/reply", accessToken);
        peticion.Content = JsonContent.Create(new SolicitudRespuestaGraph(
            cuerpoHtml, ConstruirMensajeConAdjuntos(adjuntos)));

        try
        {
            using var respuesta = await httpClient.SendAsync(peticion, cancellationToken);
            if (respuesta.IsSuccessStatusCode)
                return Result.Exito();

            var cuerpoError = await respuesta.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "Microsoft Graph devolvió {StatusCode} al responder al mensaje {MensajeId}: {CodigoError}",
                (int)respuesta.StatusCode, mensajeExternoIdOrigen, CodigoErrorGraph.Extraer(cuerpoError));
            return Result.Fallo(Error.Crear("Integraciones.Microsoft365.ErrorEnvio", "No pudimos enviar la respuesta."));
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al responder al mensaje {MensajeId} vía Microsoft Graph.", mensajeExternoIdOrigen);
            return Result.Fallo(Error.Crear("Integraciones.Microsoft365.ErrorRed", "No pudimos conectar con Microsoft."));
        }
    }

    public async Task<Result<MensajeEnviadoGraphDto>> EnviarNuevoMensajeAsync(
        string accessToken, BuzonGraph buzon, IReadOnlyList<string> destinatarios, string asunto, string cuerpoHtml,
        IReadOnlyList<AdjuntoParaEnviarDto>? adjuntos, Func<CancellationToken, Task<bool>> puertaAntesDeEnviar,
        CancellationToken cancellationToken, IReadOnlyList<string>? copia = null)
    {
        // En un buzón compartido el borrador se crea en SUS Borradores: Exchange lo envía entonces
        // como el compartido (Send As) o en su nombre (Send on Behalf), según el permiso que tenga
        // quien consintió, sin necesidad de fijar "from" a mano.
        using var peticionBorrador = NuevaPeticionGraph(HttpMethod.Post, $"{RutaBuzon(buzon)}/messages", accessToken);

        // Los Ids de mensaje de Graph NO son estables: al enviar el borrador,
        // el mensaje se mueve de Borradores a Elementos enviados y cambia de
        // Id, así que el que devuelve la creación dejaría de resolver. Esta
        // preferencia pide el Id inmutable, que sobrevive al movimiento. Si el
        // inquilino no la tiene habilitada, Graph la ignora en silencio y
        // devuelve el Id normal — se degrada al comportamiento anterior, no
        // falla. Pendiente de comprobar contra un buzón real (misma laguna que
        // el resto de la integración, Project-Hydra-Negocio/tecnico/docs/COMUNICACIONES.md § 9).
        peticionBorrador.Headers.TryAddWithoutValidation("Prefer", "IdType=\"ImmutableId\"");

        peticionBorrador.Content = JsonContent.Create(
            new MensajeNuevoGraph(
                asunto,
                new CuerpoMensajeGraphConTipo("HTML", cuerpoHtml),
                destinatarios.Select(d => new EmailAddressGraph(new DireccionGraph(d))).ToList(),
                ConstruirAdjuntosGraph(adjuntos),
                copia is { Count: > 0 } ? copia.Select(d => new EmailAddressGraph(new DireccionGraph(d))).ToList() : null));

        try
        {
            using var respuestaBorrador = await httpClient.SendAsync(peticionBorrador, cancellationToken);
            if (!respuestaBorrador.IsSuccessStatusCode)
            {
                var cuerpoError = await respuestaBorrador.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError(
                    "Microsoft Graph devolvió {StatusCode} al crear un borrador: {CodigoError}",
                    (int)respuestaBorrador.StatusCode, CodigoErrorGraph.Extraer(cuerpoError));
                // Un compartido con solo «Application Mail.Read» y «Application Mail.Send» en Exchange lee
                // y responde, pero no puede crear el borrador de un mensaje nuevo: le falta
                // «Application Mail.ReadWrite». Se nombra para que no parezca un fallo de Microsoft.
                return Result.Fallo<MensajeEnviadoGraphDto>(
                    buzon.EsCompartido && respuestaBorrador.StatusCode == System.Net.HttpStatusCode.Forbidden
                        ? Error.Crear(CodigosErrorMicrosoft365.SinPermisoEscritura,
                            "Este buzón compartido no permite enviar mensajes nuevos desde TALVEG: el administrador de Exchange debe concederle a la aplicación permiso de escritura (Mail.ReadWrite).")
                        : Error.Crear("Integraciones.Microsoft365.ErrorEnvio", "No pudimos enviar el mensaje."));
            }

            var borrador = await respuestaBorrador.Content.ReadFromJsonAsync<RespuestaCrearMensajeGraph>(cancellationToken);
            if (string.IsNullOrWhiteSpace(borrador?.Id) || string.IsNullOrWhiteSpace(borrador.ConversationId))
            {
                logger.LogError("Microsoft Graph no devolvió un Id o ConversationId válido al crear un borrador.");
                return Result.Fallo<MensajeEnviadoGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorEnvio", "No pudimos enviar el mensaje."));
            }

            var mensajeExternoId = borrador.Id!;
            var hiloExternoId = borrador.ConversationId!;

            // Puerta entre crear y enviar: crear el borrador es una llamada a Graph, y mientras tanto la conexión puede
            // haber pasado a ConError o haberse desconectado. Si ya no está operativa (o no se puede comprobar), el
            // borrador no sale: se borra y el envío falla como cualquier otro envío desde un buzón sin verificar.
            if (!await PasaPuertaAsync(puertaAntesDeEnviar, cancellationToken))
            {
                logger.LogWarning(
                    "La conexión dejó de estar operativa entre crear el borrador {MensajeId} y enviarlo: no se envía.", mensajeExternoId);
                await EliminarBorradorAsync(accessToken, buzon, mensajeExternoId, cancellationToken);
                return Result.Fallo<MensajeEnviadoGraphDto>(ErroresBuzonSinVerificar.Crear());
            }

            using var peticionEnvio = NuevaPeticionGraph(
                HttpMethod.Post, $"{RutaBuzon(buzon)}/messages/{Uri.EscapeDataString(mensajeExternoId)}/send", accessToken);
            // La preferencia va también aquí: Graph solo interpreta un Id
            // inmutable en la URL si la petición la declara. Sin esto, un Id
            // inmutable se leería como Id normal y la llamada fallaría.
            peticionEnvio.Headers.TryAddWithoutValidation("Prefer", "IdType=\"ImmutableId\"");
            using var respuestaEnvio = await httpClient.SendAsync(peticionEnvio, cancellationToken);

            if (!respuestaEnvio.IsSuccessStatusCode)
            {
                var cuerpoError = await respuestaEnvio.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError(
                    "Microsoft Graph devolvió {StatusCode} al enviar el borrador {MensajeId}: {CodigoError}",
                    (int)respuestaEnvio.StatusCode, mensajeExternoId, CodigoErrorGraph.Extraer(cuerpoError));

                await EliminarBorradorAsync(accessToken, buzon, mensajeExternoId, cancellationToken);
                return Result.Fallo<MensajeEnviadoGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorEnvio", "No pudimos enviar el mensaje."));
            }

            return Result.Exito(new MensajeEnviadoGraphDto(mensajeExternoId, hiloExternoId));
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al enviar un mensaje nuevo vía Microsoft Graph.");
            return Result.Fallo<MensajeEnviadoGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorRed", "No pudimos conectar con Microsoft."));
        }
    }

    /// <summary>
    /// Falla cerrado: una puerta que lanza (la base no responde) cuenta como no superada. La cancelación de la propia
    /// operación sí se propaga.
    /// </summary>
    private async Task<bool> PasaPuertaAsync(
        Func<CancellationToken, Task<bool>> puerta, CancellationToken cancellationToken)
    {
        try
        {
            return await puerta(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "No se pudo comprobar la conexión antes de la siguiente llamada a Graph: no se hace.");
            return false;
        }
    }

    /// <summary>
    /// Mejor esfuerzo: un borrador que no se envió no debe quedarse en Borradores del buzón. Si el borrado falla, el
    /// borrador se queda allí sin enviar; no hay reintento. La cancelación pedida no es un fallo del borrado: se propaga.
    /// </summary>
    private async Task EliminarBorradorAsync(string accessToken, BuzonGraph buzon, string mensajeExternoId, CancellationToken cancellationToken)
    {
        try
        {
            using var peticionEliminar = NuevaPeticionGraph(
                HttpMethod.Delete, $"{RutaBuzon(buzon)}/messages/{Uri.EscapeDataString(mensajeExternoId)}", accessToken);
            peticionEliminar.Headers.TryAddWithoutValidation("Prefer", "IdType=\"ImmutableId\"");
            using var respuesta = await httpClient.SendAsync(peticionEliminar, cancellationToken);
            if (!respuesta.IsSuccessStatusCode)
                logger.LogWarning(
                    "Microsoft Graph devolvió {StatusCode} al eliminar el borrador sin enviar {MensajeId}.", (int)respuesta.StatusCode, mensajeExternoId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "No se pudo eliminar el borrador huérfano {MensajeId}.", mensajeExternoId);
        }
    }

    private static MensajeConAdjuntosGraph? ConstruirMensajeConAdjuntos(IReadOnlyList<AdjuntoParaEnviarDto>? adjuntos) =>
        adjuntos is { Count: > 0 } ? new MensajeConAdjuntosGraph(ConstruirAdjuntosGraph(adjuntos)) : null;

    private static IReadOnlyList<AdjuntoEnvioGraph>? ConstruirAdjuntosGraph(IReadOnlyList<AdjuntoParaEnviarDto>? adjuntos) =>
        adjuntos is { Count: > 0 }
            ? adjuntos.Select(a => new AdjuntoEnvioGraph(a.NombreArchivo, a.TipoContenido, Convert.ToBase64String(a.Contenido))).ToList()
            : null;

    public async Task<Result<MensajeGraphDto>> ObtenerMensajeAsync(
        string accessToken, BuzonGraph buzon, string mensajeId, CancellationToken cancellationToken)
    {
        // $expand=attachments trae los metadatos de los adjuntos en el mismo
        // viaje — el contenido en sí (contentBytes) no, por eso
        // ObtenerContenidoAdjuntoAsync es una llamada aparte: descargar el
        // contenido de todos los adjuntos de un mensaje que quizás nadie
        // vaya a abrir sería desperdiciar ancho de banda en el caso común.
        var url = $"{RutaBuzon(buzon)}/messages/{Uri.EscapeDataString(mensajeId)}" +
            "?$select=subject,from,replyTo,toRecipients,ccRecipients,body,receivedDateTime,conversationId,hasAttachments,internetMessageHeaders" +
            "&$expand=attachments($select=id,name,contentType,size)";
        using var peticion = NuevaPeticionGraph(HttpMethod.Get, url, accessToken);

        try
        {
            using var respuesta = await httpClient.SendAsync(peticion, cancellationToken);
            if (!respuesta.IsSuccessStatusCode)
            {
                // Un 403, un 429 o un 5xx no dicen nada del mensaje, y tratarlos como borrado perdería la
                // notificación: la ingesta los reintenta. Un 404 tampoco basta: /users/{buzón}/messages/{id} da 404
                // también si el buzón ya no existe. Solo ErrorItemNotFound dice que falta el mensaje (borrado entre la
                // notificación y la lectura); cualquier otro código de 404, o uno ilegible, puede ser del buzón y
                // deja el evento sin resolver.
                if (respuesta.StatusCode != System.Net.HttpStatusCode.NotFound)
                    return Result.Fallo<MensajeGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorApi", "No pudimos leer el mensaje."));

                var codigoError = CodigoErrorGraph.Extraer(await respuesta.Content.ReadAsStringAsync(cancellationToken));
                if (string.Equals(codigoError, CodigoGraphElementoNoEncontrado, StringComparison.Ordinal))
                    return Result.Fallo<MensajeGraphDto>(Error.Crear(CodigoMensajeNoEncontrado, "El mensaje ya no está en el buzón."));

                logger.LogWarning(
                    "Microsoft Graph devolvió 404 ({CodigoError}) al leer el mensaje {MensajeId}: puede faltar el buzón, no se da por borrado.",
                    codigoError, mensajeId);
                return Result.Fallo<MensajeGraphDto>(Error.Crear(
                    CodigosErrorMicrosoft365.BuzonNoEncontrado, "Microsoft 365 no encuentra el buzón de este mensaje."));
            }

            var mensaje = await respuesta.Content.ReadFromJsonAsync<MensajeDetalleGraph>(cancellationToken);
            if (mensaje is null)
                return Result.Fallo<MensajeGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorApi", "No pudimos leer el mensaje."));

            // Campos obligatorios (CodigosErrorMicrosoft365.MensajeIncompleto): sin ellos no se inventa remitente, fecha
            // ni hilo; el evento espera sin gastar intento y sin marcarse procesado.
            if (CampoObligatorioAusente(mensaje) is { } campoAusente)
            {
                logger.LogWarning(
                    "Microsoft Graph devolvió el mensaje {MensajeId} sin {Campo}: se reintentará sin darlo por procesado.",
                    mensajeId, campoAusente);
                return Result.Fallo<MensajeGraphDto>(Error.Crear(
                    CodigosErrorMicrosoft365.MensajeIncompleto, "Microsoft 365 devolvió el mensaje incompleto."));
            }

            var participantes = new List<ParticipanteGraphDto>();
            if (!string.IsNullOrWhiteSpace(mensaje.From?.EmailAddress?.Address))
                participantes.Add(new ParticipanteGraphDto(mensaje.From.EmailAddress.Address, RolParticipante.De));
            // Cada elemento de las listas puede venir null (`"toRecipients":[null]` es JSON válido): se salta, no rompe el lote.
            participantes.AddRange((mensaje.ToRecipients ?? [])
                .Where(r => !string.IsNullOrWhiteSpace(r?.EmailAddress?.Address))
                .Select(r => new ParticipanteGraphDto(r!.EmailAddress!.Address!, RolParticipante.Para)));
            participantes.AddRange((mensaje.CcRecipients ?? [])
                .Where(r => !string.IsNullOrWhiteSpace(r?.EmailAddress?.Address))
                .Select(r => new ParticipanteGraphDto(r!.EmailAddress!.Address!, RolParticipante.Cc)));

            var asunto = string.IsNullOrWhiteSpace(mensaje.Subject) ? "(sin asunto)" : mensaje.Subject;

            var adjuntos = (mensaje.Attachments ?? [])
                .Where(a => !string.IsNullOrWhiteSpace(a?.Id) && !string.IsNullOrWhiteSpace(a.Name))
                .Select(a => new AdjuntoGraphDto(a!.Id!, a.Name!, a.ContentType ?? "application/octet-stream", a.Size ?? 0))
                .ToList();

            return Result.Exito(new MensajeGraphDto(
                MensajeExternoId: mensajeId,
                HiloExternoId: mensaje.ConversationId ?? mensajeId,
                Asunto: asunto,
                Remitente: mensaje.From?.EmailAddress?.Address ?? "desconocido@microsoft365.local",
                CuerpoHtml: mensaje.Body?.Content ?? string.Empty,
                FechaUtc: mensaje.ReceivedDateTime ?? DateTime.UtcNow,
                Participantes: participantes,
                Adjuntos: adjuntos,
                Cabeceras: mensaje.InternetMessageHeaders?.Where(c => c is not null)
                    .Select(c => new CabeceraCorreoDto(c!.Name ?? string.Empty, c.Value ?? string.Empty)).ToList(),
                ResponderA: (mensaje.ReplyTo ?? [])
                    .Where(r => !string.IsNullOrWhiteSpace(r?.EmailAddress?.Address))
                    .Select(r => r!.EmailAddress!.Address!).ToList()));
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al leer el mensaje {MensajeId} vía Microsoft Graph.", mensajeId);
            return Result.Fallo<MensajeGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorRed", "No pudimos conectar con Microsoft."));
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Microsoft Graph no respondió a tiempo al leer el mensaje {MensajeId}.", mensajeId);
            return Result.Fallo<MensajeGraphDto>(ErrorTiempoAgotado);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            // Un 200 con un cuerpo ilegible es un fallo de Graph como otro cualquiera: se devuelve, para que la ingesta
            // siga con los demás mensajes del lote y reintente este.
            logger.LogError(ex, "Microsoft Graph devolvió un mensaje {MensajeId} ilegible.", mensajeId);
            return Result.Fallo<MensajeGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorApi", "No pudimos leer el mensaje."));
        }
    }

    /// <summary>El primer campo obligatorio que falta (ver <see cref="CodigosErrorMicrosoft365.MensajeIncompleto"/>), o null.</summary>
    private static string? CampoObligatorioAusente(MensajeDetalleGraph mensaje)
    {
        if (string.IsNullOrWhiteSpace(mensaje.From?.EmailAddress?.Address))
            return "from.emailAddress.address";
        if (mensaje.ReceivedDateTime is null)
            return "receivedDateTime";
        if (string.IsNullOrWhiteSpace(mensaje.ConversationId))
            return "conversationId";
        if (mensaje.Body?.Content is null)
            return "body.content";
        if (mensaje.Attachments is null)
            return "attachments";
        if (mensaje.Attachments.Any(a => a is null || string.IsNullOrWhiteSpace(a.Id) || string.IsNullOrWhiteSpace(a.Name)))
            return "attachments[].id/name";
        return null;
    }

    private const string CodigoMensajeNoEncontrado = CodigosErrorMicrosoft365.MensajeNoEncontrado;

    /// <summary>Código de Graph (Exchange) para un elemento que no está en el buzón; el del buzón inexistente es otro.</summary>
    internal const string CodigoGraphElementoNoEncontrado = "ErrorItemNotFound";

    public async Task<Result<byte[]>> ObtenerContenidoAdjuntoAsync(
        string accessToken, BuzonGraph buzon, string mensajeId, string adjuntoExternoId, CancellationToken cancellationToken)
    {
        // $value devuelve el binario crudo — a diferencia de $select=contentBytes
        // (que envolvía el contenido en JSON como base64, con ~33% de
        // amplificación más el propio JSON completo en memoria antes de poder
        // empezar a decodificar nada), esto permite copiar con límite real
        // mientras se descarga en vez de confiar solo en el tamaño declarado
        // por el llamador (auditoría módulo 6).
        var url = $"{RutaBuzon(buzon)}/messages/" +
            $"{Uri.EscapeDataString(mensajeId)}/attachments/{Uri.EscapeDataString(adjuntoExternoId)}/$value";
        using var peticion = NuevaPeticionGraph(HttpMethod.Get, url, accessToken);

        try
        {
            using var respuesta = await httpClient.SendAsync(peticion, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!respuesta.IsSuccessStatusCode)
                return Result.Fallo<byte[]>(Error.Crear("Integraciones.Microsoft365.ErrorApi", "No pudimos descargar este adjunto."));

            await using var flujo = await respuesta.Content.ReadAsStreamAsync(cancellationToken);
            using var destino = new MemoryStream();
            var lote = new byte[81920];
            int leidos;
            while ((leidos = await flujo.ReadAsync(lote, cancellationToken)) > 0)
            {
                if (destino.Length + leidos > LimitesAdjuntosCorreo.TamanoMaximoDescargaBytes)
                {
                    return Result.Fallo<byte[]>(Error.Crear(
                        "Integraciones.Microsoft365.AdjuntoDemasiadoGrande", "El adjunto supera el tamaño máximo admitido."));
                }

                destino.Write(lote, 0, leidos);
            }

            return Result.Exito(destino.ToArray());
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al descargar el adjunto {AdjuntoId} del mensaje {MensajeId} vía Microsoft Graph.", adjuntoExternoId, mensajeId);
            return Result.Fallo<byte[]>(Error.Crear("Integraciones.Microsoft365.ErrorRed", "No pudimos conectar con Microsoft."));
        }
    }

    public async Task<Result<IReadOnlyList<CarpetaGraphDto>>> ListarCarpetasAsync(
        string accessToken, BuzonGraph buzon, Func<CancellationToken, Task<bool>> puertaEntrePeticiones, CancellationToken cancellationToken)
    {
        const string seleccion = "$select=id,displayName,parentFolderId,unreadItemCount,totalItemCount";

        try
        {
            var carpetasNivel1 = await ObtenerPaginaCarpetasAsync(
                accessToken, $"{RutaBuzon(buzon)}/mailFolders?{seleccion}&$top=100", cancellationToken);
            if (carpetasNivel1.EsFallido)
                return Result.Fallo<IReadOnlyList<CarpetaGraphDto>>(carpetasNivel1.Error);

            var resultado = new List<CarpetaGraphDto>(carpetasNivel1.Valor);

            // Un nivel de hijas por carpeta — suficiente para la estructura
            // real de un buzón de gestión CAE (ver comentario de la interfaz).
            foreach (var carpeta in carpetasNivel1.Valor)
            {
                // Puerta antes de cada petición de hijas: la conexión pudo pasar a ConError o desconectarse mientras
                // tanto, y entonces no se sigue leyendo el buzón ni se devuelve lo ya leído.
                if (!await PasaPuertaAsync(puertaEntrePeticiones, cancellationToken))
                {
                    logger.LogWarning("La conexión dejó de estar operativa mientras se listaban las carpetas del buzón: se corta.");
                    return Result.Fallo<IReadOnlyList<CarpetaGraphDto>>(ErroresBuzonSinVerificar.Crear());
                }

                var hijas = await ObtenerPaginaCarpetasAsync(
                    accessToken,
                    $"{RutaBuzon(buzon)}/mailFolders/{Uri.EscapeDataString(carpeta.CarpetaExternoId)}/childFolders?{seleccion}&$top=100",
                    cancellationToken);

                if (hijas.EsExitoso)
                    resultado.AddRange(hijas.Valor);
            }

            return Result.Exito<IReadOnlyList<CarpetaGraphDto>>(resultado);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al listar carpetas del buzón vía Microsoft Graph.");
            return Result.Fallo<IReadOnlyList<CarpetaGraphDto>>(Error.Crear("Integraciones.Microsoft365.ErrorRed", "No pudimos conectar con Microsoft."));
        }
    }

    private async Task<Result<IReadOnlyList<CarpetaGraphDto>>> ObtenerPaginaCarpetasAsync(string accessToken, string url, CancellationToken cancellationToken)
    {
        using var peticion = NuevaPeticionGraph(HttpMethod.Get, url, accessToken);
        using var respuesta = await httpClient.SendAsync(peticion, cancellationToken);

        if (!respuesta.IsSuccessStatusCode)
            return Result.Fallo<IReadOnlyList<CarpetaGraphDto>>(Error.Crear("Integraciones.Microsoft365.ErrorApi", "No pudimos leer las carpetas del buzón."));

        var lista = await respuesta.Content.ReadFromJsonAsync<ListaCarpetasGraph>(cancellationToken);
        var carpetas = (lista?.Value ?? [])
            .Where(c => !string.IsNullOrWhiteSpace(c.Id) && !string.IsNullOrWhiteSpace(c.DisplayName))
            .Select(c => new CarpetaGraphDto(c.Id!, c.DisplayName!, c.ParentFolderId, c.UnreadItemCount ?? 0, c.TotalItemCount ?? 0))
            .ToList();

        return Result.Exito<IReadOnlyList<CarpetaGraphDto>>(carpetas);
    }

    public async Task<Result<IReadOnlyList<MensajeResumenGraphDto>>> ListarMensajesAsync(
        string accessToken, BuzonGraph buzon, string carpetaExternoId, int top, int skip, CancellationToken cancellationToken)
    {
        var url = $"{RutaBuzon(buzon)}/mailFolders/{Uri.EscapeDataString(carpetaExternoId)}/messages" +
            "?$select=id,subject,from,receivedDateTime,conversationId,hasAttachments" +
            $"&$orderby=receivedDateTime desc&$top={top}&$skip={skip}";
        using var peticion = NuevaPeticionGraph(HttpMethod.Get, url, accessToken);

        try
        {
            using var respuesta = await httpClient.SendAsync(peticion, cancellationToken);
            if (!respuesta.IsSuccessStatusCode)
                return Result.Fallo<IReadOnlyList<MensajeResumenGraphDto>>(Error.Crear("Integraciones.Microsoft365.ErrorApi", "No pudimos leer el historial de esta carpeta."));

            var lista = await respuesta.Content.ReadFromJsonAsync<ListaMensajesResumenGraph>(cancellationToken);
            var mensajes = (lista?.Value ?? [])
                .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                .Select(m => new MensajeResumenGraphDto(
                    MensajeExternoId: m.Id!,
                    HiloExternoId: m.ConversationId ?? m.Id!,
                    Asunto: string.IsNullOrWhiteSpace(m.Subject) ? "(sin asunto)" : m.Subject,
                    Remitente: m.From?.EmailAddress?.Address ?? "desconocido@microsoft365.local",
                    FechaUtc: m.ReceivedDateTime ?? DateTime.UtcNow,
                    TieneAdjuntos: m.HasAttachments ?? false))
                .ToList();

            return Result.Exito<IReadOnlyList<MensajeResumenGraphDto>>(mensajes);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al listar mensajes de la carpeta {CarpetaId} vía Microsoft Graph.", carpetaExternoId);
            return Result.Fallo<IReadOnlyList<MensajeResumenGraphDto>>(Error.Crear("Integraciones.Microsoft365.ErrorRed", "No pudimos conectar con Microsoft."));
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Microsoft Graph no respondió a tiempo al listar mensajes de la carpeta {CarpetaId}.", carpetaExternoId);
            return Result.Fallo<IReadOnlyList<MensajeResumenGraphDto>>(ErrorTiempoAgotado);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            logger.LogError(ex, "Microsoft Graph devolvió un historial ilegible de la carpeta {CarpetaId}.", carpetaExternoId);
            return Result.Fallo<IReadOnlyList<MensajeResumenGraphDto>>(Error.Crear("Integraciones.Microsoft365.ErrorApi", "No pudimos leer el historial de esta carpeta."));
        }
    }

    public async Task<Result<SuscripcionGraphDto>> CrearSuscripcionAsync(
        string accessToken, BuzonGraph buzon, string notificationUrl, string clientState, CancellationToken cancellationToken)
    {
        // Microsoft Learn (outlook-change-notifications-overview, «Request permissions»): un buzón
        // compartido solo se puede suscribir con permiso de aplicación, así que el accessToken de un
        // compartido es el de aplicación de su directorio; el del buzón propio es el delegado.
        var expiracion = DateTime.UtcNow.Add(DuracionMaximaSuscripcion);
        var solicitud = new SolicitudSuscripcionGraph(
            "created,updated", notificationUrl, $"{RecursoBuzon(buzon)}/mailFolders('Inbox')/messages", expiracion, clientState);

        using var peticion = NuevaPeticionGraph(HttpMethod.Post, "https://graph.microsoft.com/v1.0/subscriptions", accessToken);
        peticion.Content = JsonContent.Create(solicitud);

        try
        {
            using var respuesta = await httpClient.SendAsync(peticion, cancellationToken);
            if (!respuesta.IsSuccessStatusCode)
            {
                var cuerpoError = await respuesta.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError(
                    "Microsoft Graph devolvió {StatusCode} al crear la suscripción del buzón: {CodigoError}",
                    (int)respuesta.StatusCode, CodigoErrorGraph.Extraer(cuerpoError));
                // 429/5xx es transitorio: rehabilitar una conexión espera en vez de dejarla en ConError
                // (SuscripcionAlRehabilitarService). El resto (403, 400 de validación del webhook) es definitivo.
                return Result.Fallo<SuscripcionGraphDto>(EsRespuestaTransitoria(respuesta.StatusCode)
                    ? ErrorTransitorio
                    : Error.Crear("Integraciones.Microsoft365.ErrorSuscripcion", "No pudimos activar las notificaciones de este buzón."));
            }

            var suscripcion = await respuesta.Content.ReadFromJsonAsync<SuscripcionDetalleGraph>(cancellationToken);
            if (suscripcion?.Id is null || suscripcion.ExpirationDateTime is null)
                return Result.Fallo<SuscripcionGraphDto>(ErrorTransitorio);

            return Result.Exito(new SuscripcionGraphDto(suscripcion.Id, suscripcion.ExpirationDateTime.Value));
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al crear la suscripción del buzón vía Microsoft Graph.");
            return Result.Fallo<SuscripcionGraphDto>(Error.Crear(CodigosErrorMicrosoft365.ErrorRed, "No pudimos conectar con Microsoft."));
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Microsoft Graph no respondió a tiempo al crear la suscripción del buzón.");
            return Result.Fallo<SuscripcionGraphDto>(ErrorTiempoAgotado);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // Un 200 ilegible no dice si la suscripción existe: transitorio, para no dejar la conexión en ConError.
            logger.LogError(ex, "Microsoft Graph devolvió una suscripción ilegible.");
            return Result.Fallo<SuscripcionGraphDto>(ErrorTransitorio);
        }
    }

    public string? UrlNotificacion(Guid conexionId) =>
        string.IsNullOrWhiteSpace(opciones.Value.UrlPublicaBase)
            ? null
            : $"{opciones.Value.UrlPublicaBase.TrimEnd('/')}/api/integraciones/webhooks/microsoft365/{conexionId}";

    public async Task<Result<SuscripcionGraphDto>> RenovarSuscripcionAsync(
        string accessToken, string graphSubscriptionId, CancellationToken cancellationToken)
    {
        var expiracion = DateTime.UtcNow.Add(DuracionMaximaSuscripcion);
        using var peticion = NuevaPeticionGraph(
            HttpMethod.Patch, $"https://graph.microsoft.com/v1.0/subscriptions/{Uri.EscapeDataString(graphSubscriptionId)}", accessToken);
        peticion.Content = JsonContent.Create(new SolicitudRenovacionGraph(expiracion));

        try
        {
            using var respuesta = await httpClient.SendAsync(peticion, cancellationToken);
            if (!respuesta.IsSuccessStatusCode)
            {
                var cuerpoError = await respuesta.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError(
                    "Microsoft Graph devolvió {StatusCode} al renovar la suscripción {SubscriptionId}: {CodigoError}",
                    (int)respuesta.StatusCode, graphSubscriptionId, CodigoErrorGraph.Extraer(cuerpoError));
                return Result.Fallo<SuscripcionGraphDto>(EsRespuestaTransitoria(respuesta.StatusCode)
                    ? ErrorTransitorio
                    : Error.Crear("Integraciones.Microsoft365.ErrorSuscripcion", "No pudimos renovar las notificaciones de este buzón."));
            }

            return Result.Exito(new SuscripcionGraphDto(graphSubscriptionId, expiracion));
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al renovar la suscripción {SubscriptionId} vía Microsoft Graph.", graphSubscriptionId);
            return Result.Fallo<SuscripcionGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorRed", "No pudimos conectar con Microsoft."));
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Microsoft Graph no respondió a tiempo al renovar la suscripción {SubscriptionId}.", graphSubscriptionId);
            return Result.Fallo<SuscripcionGraphDto>(ErrorTiempoAgotado);
        }
    }

    public async Task<Result> EliminarSuscripcionAsync(string accessToken, string graphSubscriptionId, CancellationToken cancellationToken)
    {
        using var peticion = NuevaPeticionGraph(
            HttpMethod.Delete, $"https://graph.microsoft.com/v1.0/subscriptions/{Uri.EscapeDataString(graphSubscriptionId)}", accessToken);

        try
        {
            using var respuesta = await httpClient.SendAsync(peticion, cancellationToken);
            if (respuesta.IsSuccessStatusCode || respuesta.StatusCode == System.Net.HttpStatusCode.NotFound)
                return Result.Exito(); // ya no existe también cuenta como "eliminada"

            var cuerpoError = await respuesta.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "Microsoft Graph devolvió {StatusCode} al eliminar la suscripción {SubscriptionId}: {CodigoError}",
                (int)respuesta.StatusCode, graphSubscriptionId, CodigoErrorGraph.Extraer(cuerpoError));
            return Result.Fallo(Error.Crear("Integraciones.Microsoft365.ErrorSuscripcion", "No pudimos eliminar la suscripción en Microsoft."));
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al eliminar la suscripción {SubscriptionId} vía Microsoft Graph.", graphSubscriptionId);
            return Result.Fallo(Error.Crear("Integraciones.Microsoft365.ErrorRed", "No pudimos conectar con Microsoft."));
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Resultado incierto: el borrado pudo llegar o no. Quien desconecta lo trata como fallido y deja la marca.
            logger.LogError(ex, "Microsoft Graph no respondió a tiempo al eliminar la suscripción {SubscriptionId}.", graphSubscriptionId);
            return Result.Fallo(ErrorTiempoAgotado);
        }
    }

    public IReadOnlyList<string> ExtraerMensajeIdsDeNotificacion(string payloadJson)
    {
        try
        {
            var notificacion = JsonSerializer.Deserialize<NotificacionesGraph>(payloadJson);
            return notificacion?.Value?
                .Where(n => !string.IsNullOrWhiteSpace(n?.ResourceData?.Id))
                .Select(n => n!.ResourceData!.Id!)
                .ToList() ?? [];
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Payload de notificación de webhook de Microsoft Graph no es JSON válido.");
            return [];
        }
    }

    public string? ExtraerClientStateDeNotificacion(string payloadJson)
        => ExtraerNotificacionHomogenea(payloadJson)?.ClientState;

    public string? ExtraerSubscriptionIdDeNotificacion(string payloadJson)
        => ExtraerNotificacionHomogenea(payloadJson)?.SubscriptionId;

    private NotificacionGraph? ExtraerNotificacionHomogenea(string payloadJson)
    {
        try
        {
            var notificacion = JsonSerializer.Deserialize<NotificacionesGraph>(payloadJson);
            var primera = notificacion?.Value?.FirstOrDefault();
            if (primera is null || string.IsNullOrWhiteSpace(primera.ClientState) ||
                string.IsNullOrWhiteSpace(primera.SubscriptionId))
                return null;

            var secreto = Encoding.UTF8.GetBytes(primera.ClientState);
            foreach (var elemento in notificacion!.Value!)
            {
                if (elemento is null || string.IsNullOrWhiteSpace(elemento.ClientState) ||
                    !CryptographicOperations.FixedTimeEquals(secreto, Encoding.UTF8.GetBytes(elemento.ClientState)) ||
                    !string.Equals(primera.SubscriptionId, elemento.SubscriptionId, StringComparison.Ordinal))
                    return null;
            }

            return primera;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Payload de notificación de webhook de Microsoft Graph no es JSON válido.");
            return null;
        }
    }

    private static HttpRequestMessage NuevaPeticionGraph(HttpMethod metodo, string url, string accessToken)
    {
        var peticion = new HttpRequestMessage(metodo, url);
        peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        peticion.Headers.Add("Prefer", "IdType=\"ImmutableId\"");
        return peticion;
    }

    private sealed record RespuestaTokenGraph(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn);

    private sealed record PerfilGraph(
        [property: JsonPropertyName("mail")] string? Mail,
        [property: JsonPropertyName("userPrincipalName")] string? UserPrincipalName);

    private sealed record SolicitudRespuestaGraph(
        [property: JsonPropertyName("comment")] string Comment,
        [property: JsonPropertyName("message")] MensajeConAdjuntosGraph? Message = null);

    private sealed record MensajeConAdjuntosGraph(
        [property: JsonPropertyName("attachments")] IReadOnlyList<AdjuntoEnvioGraph>? Attachments);

    private sealed record AdjuntoEnvioGraph(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("contentType")] string ContentType,
        [property: JsonPropertyName("contentBytes")] string ContentBytes)
    {
        [JsonPropertyName("@odata.type")]
        public string ODataType => "#microsoft.graph.fileAttachment";
    }

    private sealed record RespuestaCrearMensajeGraph(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("conversationId")] string? ConversationId);

    private sealed record MensajeNuevoGraph(
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("body")] CuerpoMensajeGraphConTipo Body,
        [property: JsonPropertyName("toRecipients")] IReadOnlyList<EmailAddressGraph> ToRecipients,
        [property: JsonPropertyName("attachments")] IReadOnlyList<AdjuntoEnvioGraph>? Attachments,
        [property: JsonPropertyName("ccRecipients"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<EmailAddressGraph>? CcRecipients = null);

    /// <summary>sendMail exige "contentType" explícito en el body (a diferencia de /reply, que no lo pide) — de ahí este envoltorio en vez de reutilizar CuerpoMensajeGraph tal cual.</summary>
    private sealed record CuerpoMensajeGraphConTipo(
        [property: JsonPropertyName("contentType")] string ContentType,
        [property: JsonPropertyName("content")] string Content);

    private sealed record DireccionGraph([property: JsonPropertyName("address")] string? Address);

    private sealed record EmailAddressGraph([property: JsonPropertyName("emailAddress")] DireccionGraph? EmailAddress);

    private sealed record CuerpoMensajeGraph([property: JsonPropertyName("content")] string? Content);

    private sealed record AdjuntoDetalleGraph(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("contentType")] string? ContentType,
        [property: JsonPropertyName("size")] long? Size);

    private sealed record CarpetaDetalleGraph(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("displayName")] string? DisplayName,
        [property: JsonPropertyName("parentFolderId")] string? ParentFolderId,
        [property: JsonPropertyName("unreadItemCount")] int? UnreadItemCount,
        [property: JsonPropertyName("totalItemCount")] int? TotalItemCount);

    private sealed record ListaCarpetasGraph([property: JsonPropertyName("value")] IReadOnlyList<CarpetaDetalleGraph>? Value);

    private sealed record MensajeResumenGraph(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("subject")] string? Subject,
        [property: JsonPropertyName("from")] EmailAddressGraph? From,
        [property: JsonPropertyName("receivedDateTime")] DateTime? ReceivedDateTime,
        [property: JsonPropertyName("conversationId")] string? ConversationId,
        [property: JsonPropertyName("hasAttachments")] bool? HasAttachments);

    private sealed record ListaMensajesResumenGraph([property: JsonPropertyName("value")] IReadOnlyList<MensajeResumenGraph>? Value);

    private sealed record MensajeDetalleGraph(
        [property: JsonPropertyName("subject")] string? Subject,
        [property: JsonPropertyName("from")] EmailAddressGraph? From,
        [property: JsonPropertyName("toRecipients")] IReadOnlyList<EmailAddressGraph>? ToRecipients,
        [property: JsonPropertyName("ccRecipients")] IReadOnlyList<EmailAddressGraph>? CcRecipients,
        [property: JsonPropertyName("body")] CuerpoMensajeGraph? Body,
        [property: JsonPropertyName("receivedDateTime")] DateTime? ReceivedDateTime,
        [property: JsonPropertyName("conversationId")] string? ConversationId,
        [property: JsonPropertyName("hasAttachments")] bool? HasAttachments,
        [property: JsonPropertyName("attachments")] IReadOnlyList<AdjuntoDetalleGraph>? Attachments,
        [property: JsonPropertyName("internetMessageHeaders")] IReadOnlyList<CabeceraGraph>? InternetMessageHeaders,
        [property: JsonPropertyName("replyTo")] IReadOnlyList<EmailAddressGraph>? ReplyTo = null);

    private sealed record CabeceraGraph(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("value")] string? Value);

    private sealed record SolicitudSuscripcionGraph(
        [property: JsonPropertyName("changeType")] string ChangeType,
        [property: JsonPropertyName("notificationUrl")] string NotificationUrl,
        [property: JsonPropertyName("resource")] string Resource,
        [property: JsonPropertyName("expirationDateTime")] DateTime ExpirationDateTime,
        [property: JsonPropertyName("clientState")] string ClientState);

    private sealed record SolicitudRenovacionGraph([property: JsonPropertyName("expirationDateTime")] DateTime ExpirationDateTime);

    private sealed record SuscripcionDetalleGraph(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("expirationDateTime")] DateTime? ExpirationDateTime);

    private sealed record RecursoNotificacionGraph([property: JsonPropertyName("id")] string? Id);

    private sealed record NotificacionGraph(
        [property: JsonPropertyName("resourceData")] RecursoNotificacionGraph? ResourceData,
        [property: JsonPropertyName("clientState")] string? ClientState,
        [property: JsonPropertyName("subscriptionId")] string? SubscriptionId);

    private sealed record NotificacionesGraph([property: JsonPropertyName("value")] IReadOnlyList<NotificacionGraph>? Value);
}
