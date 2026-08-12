using CaeManager.Application.Integraciones;
using CaeManager.Domain.Common;

namespace CaeManager.Application.Tests.Integraciones;

/// <summary>Fake en memoria — nunca llama a red real (ver CODING_STANDARDS.md).</summary>
public class Microsoft365GraphClientFalso : IMicrosoft365GraphClient
{
    public bool FallaRefresco { get; set; }
    public bool FallaEnvio { get; set; }
    public bool FallaCreacionSuscripcion { get; set; }
    public string AccessTokenDevuelto { get; set; } = "access-token-falso";
    public string RefreshTokenDevuelto { get; set; } = "refresh-token-falso";
    public string? UltimoMensajeExternoIdRespondido { get; private set; }
    public MensajeEnviadoGraphDto MensajeEnviadoADevolver { get; set; } = new("msg-ext-falso-123", "hilo-ext-falso-456");
    public MensajeGraphDto? MensajeADevolver { get; set; }
    public IReadOnlyList<string> MensajeIdsADevolver { get; set; } = [];
    public string? ClientStateADevolver { get; set; }
    public IReadOnlyList<CarpetaGraphDto> CarpetasADevolver { get; set; } = [];
    public IReadOnlyList<MensajeResumenGraphDto> MensajesADevolver { get; set; } = [];
    public byte[] ContenidoAdjuntoADevolver { get; set; } = [];

    public string ConstruirUrlAutorizacion(string redirectUri, string state) => $"https://login.microsoftonline.com/common/authorize?state={state}";

    public Task<Result<TokensGraphDto>> IntercambiarCodigoPorTokensAsync(string code, string redirectUri, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Exito(new TokensGraphDto(AccessTokenDevuelto, RefreshTokenDevuelto)));

    public Task<Result<TokensGraphDto>> RefrescarTokensAsync(string refreshToken, CancellationToken cancellationToken) =>
        Task.FromResult(FallaRefresco
            ? Result.Fallo<TokensGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorAutenticacion", "fallo simulado"))
            : Result.Exito(new TokensGraphDto(AccessTokenDevuelto, RefreshTokenDevuelto)));

    public Task<Result<string>> ObtenerBuzonEmailAsync(string accessToken, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Exito("buzon@cliente-falso.test"));

    public Task<Result> EnviarRespuestaAsync(
        string accessToken, string buzonEmail, string mensajeExternoIdOrigen, string cuerpoHtml,
        IReadOnlyList<AdjuntoParaEnviarDto>? adjuntos, CancellationToken cancellationToken)
    {
        UltimoMensajeExternoIdRespondido = mensajeExternoIdOrigen;
        return Task.FromResult(FallaEnvio
            ? Result.Fallo(Error.Crear("Integraciones.Microsoft365.ErrorEnvio", "fallo simulado"))
            : Result.Exito());
    }

    public Task<Result<MensajeEnviadoGraphDto>> EnviarNuevoMensajeAsync(
        string accessToken, string buzonEmail, IReadOnlyList<string> destinatarios, string asunto, string cuerpoHtml,
        IReadOnlyList<AdjuntoParaEnviarDto>? adjuntos, CancellationToken cancellationToken) =>
        Task.FromResult(FallaEnvio
            ? Result.Fallo<MensajeEnviadoGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorEnvio", "fallo simulado"))
            : Result.Exito(MensajeEnviadoADevolver));

    public Task<Result<MensajeGraphDto>> ObtenerMensajeAsync(
        string accessToken, string buzonEmail, string mensajeId, CancellationToken cancellationToken) =>
        Task.FromResult(MensajeADevolver is null
            ? Result.Fallo<MensajeGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorApi", "sin mensaje configurado"))
            : Result.Exito(MensajeADevolver));

    public Task<Result<byte[]>> ObtenerContenidoAdjuntoAsync(
        string accessToken, string mensajeId, string adjuntoExternoId, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Exito(ContenidoAdjuntoADevolver));

    public Task<Result<IReadOnlyList<CarpetaGraphDto>>> ListarCarpetasAsync(string accessToken, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Exito(CarpetasADevolver));

    public Task<Result<IReadOnlyList<MensajeResumenGraphDto>>> ListarMensajesAsync(
        string accessToken, string carpetaExternoId, int top, int skip, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Exito(MensajesADevolver));

    public Task<Result<SuscripcionGraphDto>> CrearSuscripcionAsync(
        string accessToken, string buzonEmail, string notificationUrl, string clientState, CancellationToken cancellationToken) =>
        Task.FromResult(FallaCreacionSuscripcion
            ? Result.Fallo<SuscripcionGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorApi", "fallo simulado"))
            : Result.Exito(new SuscripcionGraphDto("suscripcion-falsa", DateTime.UtcNow.AddDays(3))));

    public Task<Result<SuscripcionGraphDto>> RenovarSuscripcionAsync(string accessToken, string graphSubscriptionId, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Exito(new SuscripcionGraphDto(graphSubscriptionId, DateTime.UtcNow.AddDays(3))));

    public Task<Result> EliminarSuscripcionAsync(string accessToken, string graphSubscriptionId, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Exito());

    public IReadOnlyList<string> ExtraerMensajeIdsDeNotificacion(string payloadJson) => MensajeIdsADevolver;

    public string? ExtraerClientStateDeNotificacion(string payloadJson) => ClientStateADevolver;
}
