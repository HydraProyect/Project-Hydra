using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CaeManager.Application.Integraciones;
using CaeManager.Domain.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Integraciones;

/// <summary>
/// Adaptador concreto de WhatsApp Cloud API (graph.facebook.com). A
/// diferencia de <see cref="Microsoft365GraphClient"/> no hay flujo OAuth:
/// cada llamada lleva el System User token de larga duración de la línea
/// (LineaWhatsApp.TokenAcceso), que el administrador pega al dar de alta la
/// línea y solo cambia cuando lo rota a mano en Meta.
/// </summary>
public class WhatsAppCloudApiClient(
    HttpClient httpClient,
    IOptions<WhatsAppCloudApiOptions> opciones,
    ILogger<WhatsAppCloudApiClient> logger) : IWhatsAppCloudApiClient
{
    private const string BaseGraphMeta = "https://graph.facebook.com";

    public IReadOnlyList<string> ExtraerPhoneNumberIds(string payloadJson)
    {
        var resultado = new List<string>();

        try
        {
            using var documento = JsonDocument.Parse(payloadJson);
            foreach (var value in EnumerarValues(documento))
            {
                if (value.TryGetProperty("metadata", out var metadata) &&
                    metadata.TryGetProperty("phone_number_id", out var pni) &&
                    pni.GetString() is { Length: > 0 } id &&
                    !resultado.Contains(id))
                {
                    resultado.Add(id);
                }
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Payload de webhook de WhatsApp no es JSON válido — se descarta.");
        }

        return resultado;
    }

    public NotificacionWhatsAppDto ExtraerNotificacion(string payloadJson, string phoneNumberId)
    {
        var mensajes = new List<MensajeEntranteWhatsAppDto>();
        var estados = new List<EstadoMensajeWhatsAppDto>();

        using var documento = JsonDocument.Parse(payloadJson);
        foreach (var value in EnumerarValues(documento))
        {
            if (!value.TryGetProperty("metadata", out var metadata) ||
                metadata.TryGetProperty("phone_number_id", out var pni) is false ||
                pni.GetString() != phoneNumberId)
            {
                continue;
            }

            var nombresPerfil = ExtraerNombresPerfil(value);

            if (value.TryGetProperty("messages", out var listaMensajes) && listaMensajes.ValueKind == JsonValueKind.Array)
            {
                foreach (var mensaje in listaMensajes.EnumerateArray())
                {
                    var dto = MapearMensaje(mensaje, nombresPerfil);
                    if (dto is not null) mensajes.Add(dto);
                }
            }

            if (value.TryGetProperty("statuses", out var listaEstados) && listaEstados.ValueKind == JsonValueKind.Array)
            {
                foreach (var estado in listaEstados.EnumerateArray())
                {
                    var wamid = estado.TryGetProperty("id", out var idEstado) ? idEstado.GetString() : null;
                    var literal = estado.TryGetProperty("status", out var st) ? st.GetString() : null;
                    if (wamid is null || literal is null) continue;

                    string? error = null;
                    if (estado.TryGetProperty("errors", out var errores) && errores.ValueKind == JsonValueKind.Array)
                    {
                        var primero = errores.EnumerateArray().FirstOrDefault();
                        if (primero.ValueKind == JsonValueKind.Object)
                            error = primero.TryGetProperty("title", out var titulo) ? titulo.GetString() : null;
                    }

                    estados.Add(new EstadoMensajeWhatsAppDto(wamid, literal, error));
                }
            }
        }

        return new NotificacionWhatsAppDto(phoneNumberId, mensajes, estados);
    }

    public async Task<Result<string>> EnviarTextoAsync(
        string tokenAcceso, string phoneNumberId, string telefonoDestino, string texto, CancellationToken cancellationToken)
    {
        var url = $"{BaseGraphMeta}/{opciones.Value.VersionApi}/{phoneNumberId}/messages";
        using var peticion = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new
            {
                messaging_product = "whatsapp",
                recipient_type = "individual",
                to = telefonoDestino,
                type = "text",
                text = new { preview_url = false, body = texto },
            }),
        };
        peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenAcceso);

        try
        {
            using var respuesta = await httpClient.SendAsync(peticion, cancellationToken);
            var cuerpo = await respuesta.Content.ReadAsStringAsync(cancellationToken);

            if (!respuesta.IsSuccessStatusCode)
            {
                logger.LogError("Meta devolvió {StatusCode} al enviar WhatsApp por la línea {PhoneNumberId}: {Cuerpo}",
                    (int)respuesta.StatusCode, phoneNumberId, cuerpo);
                return Result.Fallo<string>(Error.Crear(
                    "Integraciones.WhatsApp.ErrorEnvio", "No pudimos enviar el mensaje de WhatsApp."));
            }

            using var documento = JsonDocument.Parse(cuerpo);
            var wamid = documento.RootElement.TryGetProperty("messages", out var mensajes) &&
                        mensajes.ValueKind == JsonValueKind.Array &&
                        mensajes.GetArrayLength() > 0 &&
                        mensajes[0].TryGetProperty("id", out var id)
                ? id.GetString()
                : null;

            return string.IsNullOrWhiteSpace(wamid)
                ? Result.Fallo<string>(Error.Crear(
                    "Integraciones.WhatsApp.ErrorEnvio", "Meta no devolvió el identificador del mensaje enviado."))
                : Result.Exito(wamid);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al enviar WhatsApp por la línea {PhoneNumberId}.", phoneNumberId);
            return Result.Fallo<string>(Error.Crear("Integraciones.WhatsApp.ErrorRed", "No pudimos conectar con Meta."));
        }
    }

    public async Task<Result<MediaDescargadoWhatsAppDto>> DescargarMediaAsync(
        string tokenAcceso, string mediaId, CancellationToken cancellationToken)
    {
        try
        {
            // Salto 1: metadatos del media → URL efímera (~5 min) de lookaside.
            using var peticionMeta = new HttpRequestMessage(
                HttpMethod.Get, $"{BaseGraphMeta}/{opciones.Value.VersionApi}/{mediaId}");
            peticionMeta.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenAcceso);

            using var respuestaMeta = await httpClient.SendAsync(peticionMeta, cancellationToken);
            if (!respuestaMeta.IsSuccessStatusCode)
                return Result.Fallo<MediaDescargadoWhatsAppDto>(Error.Crear(
                    "Integraciones.WhatsApp.ErrorMedia", "No pudimos localizar el archivo adjunto en Meta."));

            using var metadatos = JsonDocument.Parse(await respuestaMeta.Content.ReadAsStringAsync(cancellationToken));
            var urlEfimera = metadatos.RootElement.TryGetProperty("url", out var url) ? url.GetString() : null;
            var tipoContenido = metadatos.RootElement.TryGetProperty("mime_type", out var mime)
                ? mime.GetString()
                : null;
            if (metadatos.RootElement.TryGetProperty("file_size", out var tamano) &&
                tamano.TryGetInt64(out var bytes) && bytes > LimitesMediaWhatsApp.TamanoMaximoBytes)
                return Result.Fallo<MediaDescargadoWhatsAppDto>(Error.Crear(
                    "Integraciones.WhatsApp.MediaDemasiadoGrande", "El adjunto supera el tamaño máximo admitido."));
            if (string.IsNullOrWhiteSpace(urlEfimera))
                return Result.Fallo<MediaDescargadoWhatsAppDto>(Error.Crear(
                    "Integraciones.WhatsApp.ErrorMedia", "Meta no devolvió la URL del adjunto."));

            // Salto 2: la URL de lookaside exige el mismo Bearer.
            using var peticionBinario = new HttpRequestMessage(HttpMethod.Get, urlEfimera);
            peticionBinario.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenAcceso);

            using var respuestaBinario = await httpClient.SendAsync(peticionBinario, cancellationToken);
            if (!respuestaBinario.IsSuccessStatusCode)
                return Result.Fallo<MediaDescargadoWhatsAppDto>(Error.Crear(
                    "Integraciones.WhatsApp.ErrorMedia", "No pudimos descargar el archivo adjunto de Meta."));

            var contenido = await respuestaBinario.Content.ReadAsByteArrayAsync(cancellationToken);
            return Result.Exito(new MediaDescargadoWhatsAppDto(
                contenido, tipoContenido ?? "application/octet-stream"));
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al descargar el media {MediaId} de Meta.", mediaId);
            return Result.Fallo<MediaDescargadoWhatsAppDto>(Error.Crear(
                "Integraciones.WhatsApp.ErrorRed", "No pudimos conectar con Meta."));
        }
    }

    /// <summary>entry[].changes[].value de una notificación — el sobre común de todos los webhooks de Meta.</summary>
    private static IEnumerable<JsonElement> EnumerarValues(JsonDocument documento)
    {
        if (!documento.RootElement.TryGetProperty("entry", out var entradas) ||
            entradas.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var entrada in entradas.EnumerateArray())
        {
            if (!entrada.TryGetProperty("changes", out var cambios) || cambios.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var cambio in cambios.EnumerateArray())
            {
                if (cambio.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object)
                    yield return value;
            }
        }
    }

    /// <summary>contacts[] trae wa_id → profile.name; los mensajes solo traen el wa_id en "from".</summary>
    private static Dictionary<string, string> ExtraerNombresPerfil(JsonElement value)
    {
        var nombres = new Dictionary<string, string>();

        if (!value.TryGetProperty("contacts", out var contactos) || contactos.ValueKind != JsonValueKind.Array)
            return nombres;

        foreach (var contacto in contactos.EnumerateArray())
        {
            var waId = contacto.TryGetProperty("wa_id", out var id) ? id.GetString() : null;
            var nombre = contacto.TryGetProperty("profile", out var perfil) &&
                         perfil.TryGetProperty("name", out var n)
                ? n.GetString()
                : null;
            if (waId is not null && nombre is not null)
                nombres[waId] = nombre;
        }

        return nombres;
    }

    private static MensajeEntranteWhatsAppDto? MapearMensaje(JsonElement mensaje, Dictionary<string, string> nombresPerfil)
    {
        var wamid = mensaje.TryGetProperty("id", out var id) ? id.GetString() : null;
        var waId = mensaje.TryGetProperty("from", out var from) ? from.GetString() : null;
        var tipo = mensaje.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (wamid is null || waId is null || tipo is null) return null;

        var fechaUtc = mensaje.TryGetProperty("timestamp", out var ts) &&
                       long.TryParse(ts.GetString(), out var unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
            : DateTime.UtcNow;

        string? texto = null, mediaId = null, nombreArchivo = null, tipoContenido = null;

        switch (tipo)
        {
            case "text":
                texto = mensaje.TryGetProperty("text", out var cuerpoTexto) &&
                        cuerpoTexto.TryGetProperty("body", out var body)
                    ? body.GetString()
                    : null;
                break;
            case "image" or "document":
                if (mensaje.TryGetProperty(tipo, out var media))
                {
                    mediaId = media.TryGetProperty("id", out var mid) ? mid.GetString() : null;
                    tipoContenido = media.TryGetProperty("mime_type", out var mime) ? mime.GetString() : null;
                    nombreArchivo = media.TryGetProperty("filename", out var fn) ? fn.GetString() : null;
                    texto = media.TryGetProperty("caption", out var caption) ? caption.GetString() : null;
                }
                break;
        }

        // wa_id llega sin el prefijo + (34686543364) — se normaliza a E.164.
        var telefono = waId.StartsWith('+') ? waId : $"+{waId}";
        nombresPerfil.TryGetValue(waId, out var nombrePerfil);

        return new MensajeEntranteWhatsAppDto(
            wamid, telefono, nombrePerfil, tipo, texto, mediaId, nombreArchivo, tipoContenido, fechaUtc);
    }
}
