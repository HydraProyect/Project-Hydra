using CaeManager.Application.Integraciones;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Email;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Integraciones;

/// <summary>
/// Guarda de envío delante de <see cref="Microsoft365GraphClient"/> (ver <see cref="EnvioCorreoOptions"/>).
/// Solo comprueba los dos métodos que sacan correo del buzón; ingesta, tokens y suscripciones se
/// delegan siempre, porque cerrar la salida no puede dejar a la Conexión sin entrada ni sin renovar.
///
/// <para>
/// Una respuesta a un hilo no dice aquí a quién va: lo resuelve Graph desde el mensaje de origen. Por
/// eso <see cref="ModoEnvioCorreo.SoloPermitidos"/> no la deja salir y solo sale con la guarda abierta.
/// </para>
/// </summary>
public sealed class GuardaEnvioMicrosoft365GraphClient(
    IMicrosoft365GraphClient interno,
    IOptions<EnvioCorreoOptions> opciones,
    ILogger<GuardaEnvioMicrosoft365GraphClient> logger) : IMicrosoft365GraphClient
{
    public Task<Result> EnviarRespuestaAsync(
        string accessToken, string buzonEmail, string mensajeExternoIdOrigen, string cuerpoHtml,
        IReadOnlyList<AdjuntoParaEnviarDto>? adjuntos, CancellationToken cancellationToken)
    {
        var config = opciones.Value;

        if (config.PermiteEnvioA([]))
            return interno.EnviarRespuestaAsync(accessToken, buzonEmail, mensajeExternoIdOrigen, cuerpoHtml, adjuntos, cancellationToken);

        AnotarBloqueo(config, "respuesta");
        return Task.FromResult(Result.Fallo(config.ErrorEnvioCerrado()));
    }

    public Task<Result<MensajeEnviadoGraphDto>> EnviarNuevoMensajeAsync(
        string accessToken, string buzonEmail, IReadOnlyList<string> destinatarios, string asunto, string cuerpoHtml,
        IReadOnlyList<AdjuntoParaEnviarDto>? adjuntos, CancellationToken cancellationToken,
        IReadOnlyList<string>? copia = null)
    {
        var config = opciones.Value;

        if (config.PermiteEnvioA([.. destinatarios, .. copia ?? []]))
            return interno.EnviarNuevoMensajeAsync(accessToken, buzonEmail, destinatarios, asunto, cuerpoHtml, adjuntos, cancellationToken, copia);

        AnotarBloqueo(config, "mensaje nuevo");
        return Task.FromResult(Result.Fallo<MensajeEnviadoGraphDto>(config.ErrorEnvioCerrado()));
    }

    private void AnotarBloqueo(EnvioCorreoOptions config, string envio) =>
        logger.LogWarning(EnvioCorreoOptions.TextoBloqueo + ": canal Microsoft 365, {Envio}, modo {Modo}.", envio, config.ModoEfectivo);

    public string ConstruirUrlAutorizacion(string redirectUri, string state) =>
        interno.ConstruirUrlAutorizacion(redirectUri, state);

    public Task<Result<TokensGraphDto>> IntercambiarCodigoPorTokensAsync(string code, string redirectUri, CancellationToken cancellationToken) =>
        interno.IntercambiarCodigoPorTokensAsync(code, redirectUri, cancellationToken);

    public Task<Result<TokensGraphDto>> RefrescarTokensAsync(string refreshToken, CancellationToken cancellationToken) =>
        interno.RefrescarTokensAsync(refreshToken, cancellationToken);

    public Task<Result<string>> ObtenerBuzonEmailAsync(string accessToken, CancellationToken cancellationToken) =>
        interno.ObtenerBuzonEmailAsync(accessToken, cancellationToken);

    public Task<Result<MensajeGraphDto>> ObtenerMensajeAsync(
        string accessToken, string buzonEmail, string mensajeId, CancellationToken cancellationToken) =>
        interno.ObtenerMensajeAsync(accessToken, buzonEmail, mensajeId, cancellationToken);

    public Task<Result<byte[]>> ObtenerContenidoAdjuntoAsync(
        string accessToken, string mensajeId, string adjuntoExternoId, CancellationToken cancellationToken) =>
        interno.ObtenerContenidoAdjuntoAsync(accessToken, mensajeId, adjuntoExternoId, cancellationToken);

    public Task<Result<IReadOnlyList<CarpetaGraphDto>>> ListarCarpetasAsync(string accessToken, CancellationToken cancellationToken) =>
        interno.ListarCarpetasAsync(accessToken, cancellationToken);

    public Task<Result<IReadOnlyList<MensajeResumenGraphDto>>> ListarMensajesAsync(
        string accessToken, string carpetaExternoId, int top, int skip, CancellationToken cancellationToken) =>
        interno.ListarMensajesAsync(accessToken, carpetaExternoId, top, skip, cancellationToken);

    public Task<Result<SuscripcionGraphDto>> CrearSuscripcionAsync(
        string accessToken, string buzonEmail, string notificationUrl, string clientState, CancellationToken cancellationToken) =>
        interno.CrearSuscripcionAsync(accessToken, buzonEmail, notificationUrl, clientState, cancellationToken);

    public Task<Result<SuscripcionGraphDto>> RenovarSuscripcionAsync(string accessToken, string graphSubscriptionId, CancellationToken cancellationToken) =>
        interno.RenovarSuscripcionAsync(accessToken, graphSubscriptionId, cancellationToken);

    public Task<Result> EliminarSuscripcionAsync(string accessToken, string graphSubscriptionId, CancellationToken cancellationToken) =>
        interno.EliminarSuscripcionAsync(accessToken, graphSubscriptionId, cancellationToken);

    public IReadOnlyList<string> ExtraerMensajeIdsDeNotificacion(string payloadJson) =>
        interno.ExtraerMensajeIdsDeNotificacion(payloadJson);

    public string? ExtraerClientStateDeNotificacion(string payloadJson) =>
        interno.ExtraerClientStateDeNotificacion(payloadJson);

    public string? ExtraerSubscriptionIdDeNotificacion(string payloadJson) =>
        interno.ExtraerSubscriptionIdDeNotificacion(payloadJson);
}
