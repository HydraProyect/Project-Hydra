using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CaeManager.Application.Integraciones;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Integraciones;

/// <summary>
/// Adaptador concreto de Microsoft 365 (ver ARQUITECTURA-INTEGRACIONES.md §
/// 5/12) — flujo delegado (authorization code + offline_access), no
/// client-credentials como <c>GraphEmailService</c>: aquí no hay "el propio
/// buzón de Hydra", cada <c>ConexionIntegracion</c> es el buzón real de un
/// cliente distinto, consentido por su propio administrador. Por eso todas
/// las llamadas usan <c>/me/...</c> (resuelto por el token, no por un UPN
/// en la URL) en vez de <c>/users/{upn}/...</c>.
/// </summary>
public class Microsoft365GraphClient(
    HttpClient httpClient,
    IOptions<Microsoft365GraphOptions> opciones,
    ILogger<Microsoft365GraphClient> logger) : IMicrosoft365GraphClient
{
    private const string EndpointAutorizacion = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize";
    private const string EndpointToken = "https://login.microsoftonline.com/common/oauth2/v2.0/token";
    private const string ScopesDelegados = "offline_access https://graph.microsoft.com/Mail.Read https://graph.microsoft.com/Mail.Send";

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
            ["client_id"] = opciones.Value.ClientId!,
            ["client_secret"] = opciones.Value.ClientSecret!,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["scope"] = ScopesDelegados,
        };

        return await PedirTokensAsync(contenido, cancellationToken);
    }

    public async Task<Result<TokensGraphDto>> RefrescarTokensAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var contenido = new Dictionary<string, string>
        {
            ["client_id"] = opciones.Value.ClientId!,
            ["client_secret"] = opciones.Value.ClientSecret!,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = ScopesDelegados,
        };

        return await PedirTokensAsync(contenido, cancellationToken);
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
                return Result.Fallo<TokensGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorAutenticacion", "No pudimos autenticar con Microsoft."));
            }

            var cuerpo = await respuesta.Content.ReadFromJsonAsync<RespuestaTokenGraph>(cancellationToken);
            if (cuerpo?.AccessToken is null || cuerpo.RefreshToken is null)
                return Result.Fallo<TokensGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorAutenticacion", "No pudimos autenticar con Microsoft."));

            return Result.Exito(new TokensGraphDto(cuerpo.AccessToken, cuerpo.RefreshToken));
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al canjear tokens con Microsoft Entra ID.");
            return Result.Fallo<TokensGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorRed", "No pudimos conectar con Microsoft."));
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

    public async Task<Result> EnviarRespuestaAsync(
        string accessToken, string buzonEmail, string mensajeExternoIdOrigen, string cuerpoHtml,
        IReadOnlyList<AdjuntoParaEnviarDto>? adjuntos, CancellationToken cancellationToken)
    {
        // /reply preserva conversationId/threading automáticamente — nunca
        // reconstruir In-Reply-To/References a mano (ver
        // ARQUITECTURA-INTEGRACIONES.md § 12.2). Los adjuntos van dentro de
        // "message" — es la forma en que Graph permite adjuntar algo más al
        // responder, en vez de solo el comentario de texto.
        using var peticion = NuevaPeticionGraph(
            HttpMethod.Post, $"https://graph.microsoft.com/v1.0/me/messages/{Uri.EscapeDataString(mensajeExternoIdOrigen)}/reply", accessToken);
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
        string accessToken, string buzonEmail, IReadOnlyList<string> destinatarios, string asunto, string cuerpoHtml,
        IReadOnlyList<AdjuntoParaEnviarDto>? adjuntos, CancellationToken cancellationToken)
    {
        using var peticionBorrador = NuevaPeticionGraph(HttpMethod.Post, "https://graph.microsoft.com/v1.0/me/messages", accessToken);

        // Los Ids de mensaje de Graph NO son estables: al enviar el borrador,
        // el mensaje se mueve de Borradores a Elementos enviados y cambia de
        // Id, así que el que devuelve la creación dejaría de resolver. Esta
        // preferencia pide el Id inmutable, que sobrevive al movimiento. Si el
        // inquilino no la tiene habilitada, Graph la ignora en silencio y
        // devuelve el Id normal — se degrada al comportamiento anterior, no
        // falla. Pendiente de comprobar contra un buzón real (misma laguna que
        // el resto de la integración, docs/COMUNICACIONES.md § 9).
        peticionBorrador.Headers.TryAddWithoutValidation("Prefer", "IdType=\"ImmutableId\"");

        peticionBorrador.Content = JsonContent.Create(
            new MensajeNuevoGraph(
                asunto,
                new CuerpoMensajeGraphConTipo("HTML", cuerpoHtml),
                destinatarios.Select(d => new EmailAddressGraph(new DireccionGraph(d))).ToList(),
                ConstruirAdjuntosGraph(adjuntos)));

        try
        {
            using var respuestaBorrador = await httpClient.SendAsync(peticionBorrador, cancellationToken);
            if (!respuestaBorrador.IsSuccessStatusCode)
            {
                var cuerpoError = await respuestaBorrador.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError(
                    "Microsoft Graph devolvió {StatusCode} al crear un borrador: {CodigoError}",
                    (int)respuestaBorrador.StatusCode, CodigoErrorGraph.Extraer(cuerpoError));
                return Result.Fallo<MensajeEnviadoGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorEnvio", "No pudimos enviar el mensaje."));
            }

            var borrador = await respuestaBorrador.Content.ReadFromJsonAsync<RespuestaCrearMensajeGraph>(cancellationToken);
            if (string.IsNullOrWhiteSpace(borrador?.Id) || string.IsNullOrWhiteSpace(borrador.ConversationId))
            {
                logger.LogError("Microsoft Graph no devolvió un Id o ConversationId válido al crear un borrador.");
                return Result.Fallo<MensajeEnviadoGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorEnvio", "No pudimos enviar el mensaje."));
            }

            var mensajeExternoId = borrador.Id!;
            var hiloExternoId = borrador.ConversationId!;

            using var peticionEnvio = NuevaPeticionGraph(
                HttpMethod.Post, $"https://graph.microsoft.com/v1.0/me/messages/{Uri.EscapeDataString(mensajeExternoId)}/send", accessToken);
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

                try
                {
                    using var peticionEliminar = NuevaPeticionGraph(
                        HttpMethod.Delete, $"https://graph.microsoft.com/v1.0/me/messages/{Uri.EscapeDataString(mensajeExternoId)}", accessToken);
                    peticionEliminar.Headers.TryAddWithoutValidation("Prefer", "IdType=\"ImmutableId\"");
                    await httpClient.SendAsync(peticionEliminar, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "No se pudo eliminar el borrador huérfano {MensajeId}.", mensajeExternoId);
                }

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

    private static MensajeConAdjuntosGraph? ConstruirMensajeConAdjuntos(IReadOnlyList<AdjuntoParaEnviarDto>? adjuntos) =>
        adjuntos is { Count: > 0 } ? new MensajeConAdjuntosGraph(ConstruirAdjuntosGraph(adjuntos)) : null;

    private static IReadOnlyList<AdjuntoEnvioGraph>? ConstruirAdjuntosGraph(IReadOnlyList<AdjuntoParaEnviarDto>? adjuntos) =>
        adjuntos is { Count: > 0 }
            ? adjuntos.Select(a => new AdjuntoEnvioGraph(a.NombreArchivo, a.TipoContenido, Convert.ToBase64String(a.Contenido))).ToList()
            : null;

    public async Task<Result<MensajeGraphDto>> ObtenerMensajeAsync(
        string accessToken, string buzonEmail, string mensajeId, CancellationToken cancellationToken)
    {
        // $expand=attachments trae los metadatos de los adjuntos en el mismo
        // viaje — el contenido en sí (contentBytes) no, por eso
        // ObtenerContenidoAdjuntoAsync es una llamada aparte: descargar el
        // contenido de todos los adjuntos de un mensaje que quizás nadie
        // vaya a abrir sería desperdiciar ancho de banda en el caso común.
        var url = $"https://graph.microsoft.com/v1.0/me/messages/{Uri.EscapeDataString(mensajeId)}" +
            "?$select=subject,from,toRecipients,ccRecipients,body,receivedDateTime,conversationId,hasAttachments" +
            "&$expand=attachments($select=id,name,contentType,size)";
        using var peticion = NuevaPeticionGraph(HttpMethod.Get, url, accessToken);

        try
        {
            using var respuesta = await httpClient.SendAsync(peticion, cancellationToken);
            if (!respuesta.IsSuccessStatusCode)
                return Result.Fallo<MensajeGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorApi", "No pudimos leer el mensaje."));

            var mensaje = await respuesta.Content.ReadFromJsonAsync<MensajeDetalleGraph>(cancellationToken);
            if (mensaje is null)
                return Result.Fallo<MensajeGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorApi", "No pudimos leer el mensaje."));

            var participantes = new List<ParticipanteGraphDto>();
            if (!string.IsNullOrWhiteSpace(mensaje.From?.EmailAddress?.Address))
                participantes.Add(new ParticipanteGraphDto(mensaje.From.EmailAddress.Address, RolParticipante.De));
            participantes.AddRange((mensaje.ToRecipients ?? [])
                .Where(r => !string.IsNullOrWhiteSpace(r.EmailAddress?.Address))
                .Select(r => new ParticipanteGraphDto(r.EmailAddress!.Address!, RolParticipante.Para)));
            participantes.AddRange((mensaje.CcRecipients ?? [])
                .Where(r => !string.IsNullOrWhiteSpace(r.EmailAddress?.Address))
                .Select(r => new ParticipanteGraphDto(r.EmailAddress!.Address!, RolParticipante.Cc)));

            var asunto = string.IsNullOrWhiteSpace(mensaje.Subject) ? "(sin asunto)" : mensaje.Subject;

            var adjuntos = (mensaje.Attachments ?? [])
                .Where(a => !string.IsNullOrWhiteSpace(a.Id) && !string.IsNullOrWhiteSpace(a.Name))
                .Select(a => new AdjuntoGraphDto(a.Id!, a.Name!, a.ContentType ?? "application/octet-stream", a.Size ?? 0))
                .ToList();

            return Result.Exito(new MensajeGraphDto(
                MensajeExternoId: mensajeId,
                HiloExternoId: mensaje.ConversationId ?? mensajeId,
                Asunto: asunto,
                Remitente: mensaje.From?.EmailAddress?.Address ?? "desconocido@microsoft365.local",
                CuerpoHtml: mensaje.Body?.Content ?? string.Empty,
                FechaUtc: mensaje.ReceivedDateTime ?? DateTime.UtcNow,
                Participantes: participantes,
                Adjuntos: adjuntos));
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al leer el mensaje {MensajeId} vía Microsoft Graph.", mensajeId);
            return Result.Fallo<MensajeGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorRed", "No pudimos conectar con Microsoft."));
        }
    }

    public async Task<Result<byte[]>> ObtenerContenidoAdjuntoAsync(
        string accessToken, string mensajeId, string adjuntoExternoId, CancellationToken cancellationToken)
    {
        // $value devuelve el binario crudo — a diferencia de $select=contentBytes
        // (que envolvía el contenido en JSON como base64, con ~33% de
        // amplificación más el propio JSON completo en memoria antes de poder
        // empezar a decodificar nada), esto permite copiar con límite real
        // mientras se descarga en vez de confiar solo en el tamaño declarado
        // por el llamador (auditoría módulo 6).
        var url = "https://graph.microsoft.com/v1.0/me/messages/" +
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

    public async Task<Result<IReadOnlyList<CarpetaGraphDto>>> ListarCarpetasAsync(string accessToken, CancellationToken cancellationToken)
    {
        const string seleccion = "$select=id,displayName,parentFolderId,unreadItemCount,totalItemCount";

        try
        {
            var carpetasNivel1 = await ObtenerPaginaCarpetasAsync(
                accessToken, $"https://graph.microsoft.com/v1.0/me/mailFolders?{seleccion}&$top=100", cancellationToken);
            if (carpetasNivel1.EsFallido)
                return Result.Fallo<IReadOnlyList<CarpetaGraphDto>>(carpetasNivel1.Error);

            var resultado = new List<CarpetaGraphDto>(carpetasNivel1.Valor);

            // Un nivel de hijas por carpeta — suficiente para la estructura
            // real de un buzón de gestión CAE (ver comentario de la interfaz).
            foreach (var carpeta in carpetasNivel1.Valor)
            {
                var hijas = await ObtenerPaginaCarpetasAsync(
                    accessToken,
                    $"https://graph.microsoft.com/v1.0/me/mailFolders/{Uri.EscapeDataString(carpeta.CarpetaExternoId)}/childFolders?{seleccion}&$top=100",
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
        string accessToken, string carpetaExternoId, int top, int skip, CancellationToken cancellationToken)
    {
        var url = $"https://graph.microsoft.com/v1.0/me/mailFolders/{Uri.EscapeDataString(carpetaExternoId)}/messages" +
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
    }

    public async Task<Result<SuscripcionGraphDto>> CrearSuscripcionAsync(
        string accessToken, string buzonEmail, string notificationUrl, string clientState, CancellationToken cancellationToken)
    {
        var expiracion = DateTime.UtcNow.Add(DuracionMaximaSuscripcion);
        var solicitud = new SolicitudSuscripcionGraph(
            "created,updated", notificationUrl, "me/mailFolders('Inbox')/messages", expiracion, clientState);

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
                return Result.Fallo<SuscripcionGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorSuscripcion", "No pudimos activar las notificaciones de este buzón."));
            }

            var suscripcion = await respuesta.Content.ReadFromJsonAsync<SuscripcionDetalleGraph>(cancellationToken);
            if (suscripcion?.Id is null || suscripcion.ExpirationDateTime is null)
                return Result.Fallo<SuscripcionGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorSuscripcion", "No pudimos activar las notificaciones de este buzón."));

            return Result.Exito(new SuscripcionGraphDto(suscripcion.Id, suscripcion.ExpirationDateTime.Value));
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al crear la suscripción del buzón vía Microsoft Graph.");
            return Result.Fallo<SuscripcionGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorRed", "No pudimos conectar con Microsoft."));
        }
    }

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
                return Result.Fallo<SuscripcionGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorSuscripcion", "No pudimos renovar las notificaciones de este buzón."));
            }

            return Result.Exito(new SuscripcionGraphDto(graphSubscriptionId, expiracion));
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al renovar la suscripción {SubscriptionId} vía Microsoft Graph.", graphSubscriptionId);
            return Result.Fallo<SuscripcionGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorRed", "No pudimos conectar con Microsoft."));
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
    }

    public IReadOnlyList<string> ExtraerMensajeIdsDeNotificacion(string payloadJson)
    {
        try
        {
            var notificacion = JsonSerializer.Deserialize<NotificacionesGraph>(payloadJson);
            return notificacion?.Value?
                .Where(n => !string.IsNullOrWhiteSpace(n.ResourceData?.Id))
                .Select(n => n.ResourceData!.Id!)
                .ToList() ?? [];
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Payload de notificación de webhook de Microsoft Graph no es JSON válido.");
            return [];
        }
    }

    public string? ExtraerClientStateDeNotificacion(string payloadJson)
    {
        try
        {
            var notificacion = JsonSerializer.Deserialize<NotificacionesGraph>(payloadJson);
            return notificacion?.Value?.FirstOrDefault()?.ClientState;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Payload de notificación de webhook de Microsoft Graph no es JSON válido.");
            return null;
        }
    }

    public string? ExtraerSubscriptionIdDeNotificacion(string payloadJson)
    {
        try
        {
            var notificacion = JsonSerializer.Deserialize<NotificacionesGraph>(payloadJson);
            return notificacion?.Value?.FirstOrDefault()?.SubscriptionId;
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
        [property: JsonPropertyName("refresh_token")] string? RefreshToken);

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
        [property: JsonPropertyName("attachments")] IReadOnlyList<AdjuntoEnvioGraph>? Attachments);

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
        [property: JsonPropertyName("attachments")] IReadOnlyList<AdjuntoDetalleGraph>? Attachments);

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
