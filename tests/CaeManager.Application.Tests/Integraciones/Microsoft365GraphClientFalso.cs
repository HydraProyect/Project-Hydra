using CaeManager.Application.Integraciones;
using CaeManager.Domain.Common;

namespace CaeManager.Application.Tests.Integraciones;

/// <summary>Fake en memoria — nunca llama a red real (ver Project-Hydra-Negocio/tecnico/CODING_STANDARDS.md).</summary>
public class Microsoft365GraphClientFalso : IMicrosoft365GraphClient
{
    public bool FallaRefresco { get; set; }
    public bool FallaEnvio { get; set; }

    /// <summary>Llamadas a cualquiera de los dos envíos: la cuenta que prueba que algo salió, o que no.</summary>
    public int VecesEnviado { get; private set; }

    public bool FallaCreacionSuscripcion { get; set; }
    public string AccessTokenDevuelto { get; set; } = "access-token-falso";
    public string RefreshTokenDevuelto { get; set; } = "refresh-token-falso";
    public DateTime AccessTokenExpiraUtcDevuelto { get; set; } = DateTime.UtcNow.AddHours(1);
    public string? UltimoMensajeExternoIdRespondido { get; private set; }
    public MensajeEnviadoGraphDto MensajeEnviadoADevolver { get; set; } = new("msg-ext-falso-123", "hilo-ext-falso-456");
    public MensajeGraphDto? MensajeADevolver { get; set; }

    /// <summary>Para una notificación con varios mensajes; el que no está aquí cae en <see cref="MensajeADevolver"/>.</summary>
    public Dictionary<string, MensajeGraphDto> MensajesPorId { get; } = [];

    public string? UltimoCuerpoRespondido { get; private set; }
    public IReadOnlyList<string> MensajeIdsADevolver { get; set; } = [];
    public string? ClientStateADevolver { get; set; }
    public string? SubscriptionIdADevolver { get; set; }
    public IReadOnlyList<CarpetaGraphDto> CarpetasADevolver { get; set; } = [];
    public IReadOnlyList<MensajeResumenGraphDto> MensajesADevolver { get; set; } = [];
    public byte[] ContenidoAdjuntoADevolver { get; set; } = [];

    public string ConstruirUrlAutorizacion(string redirectUri, string state) => $"https://login.microsoftonline.com/common/authorize?state={state}";

    public Task<Result<TokensGraphDto>> IntercambiarCodigoPorTokensAsync(string code, string redirectUri, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Exito(new TokensGraphDto(AccessTokenDevuelto, RefreshTokenDevuelto, AccessTokenExpiraUtcDevuelto)));

    public int VecesRefrescado { get; private set; }

    public Task<Result<TokensGraphDto>> RefrescarTokensAsync(string refreshToken, CancellationToken cancellationToken)
    {
        VecesRefrescado++;
        return Task.FromResult(FallaRefresco
            ? Result.Fallo<TokensGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorAutenticacion", "fallo simulado"))
            : Result.Exito(new TokensGraphDto(AccessTokenDevuelto, RefreshTokenDevuelto, AccessTokenExpiraUtcDevuelto)));
    }

    public List<string> DirectoriosConTokenDeAplicacion { get; } = [];

    public Task<Result<TokenAplicacionGraphDto>> ObtenerTokenAplicacionAsync(string directorioId, CancellationToken cancellationToken)
    {
        DirectoriosConTokenDeAplicacion.Add(directorioId);
        return Task.FromResult(Result.Exito(new TokenAplicacionGraphDto("token-aplicacion-falso", DateTime.UtcNow.AddHours(1))));
    }

    public Task<Result<string>> ObtenerBuzonEmailAsync(string accessToken, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Exito("buzon@cliente-falso.test"));

    public string DirectorioDevuelto { get; set; } = "11111111-2222-3333-4444-555555555555";

    public Task<Result<string>> ObtenerDirectorioAsync(string accessToken, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Exito(DirectorioDevuelto));

    /// <summary>Cada buzón sobre el que se pidió algo a Graph, en orden: prueba que una conexión compartida no cae en /me.</summary>
    public List<BuzonGraph> BuzonesUsados { get; } = [];

    public int VecesSuscrito { get; private set; }

    public Task<Result> ComprobarAccesoBuzonAsync(string accessToken, BuzonGraph buzon, CancellationToken cancellationToken)
    {
        BuzonesUsados.Add(buzon);
        return Task.FromResult(Result.Exito());
    }

    public Task<Result> EnviarRespuestaAsync(
        string accessToken, BuzonGraph buzon, string mensajeExternoIdOrigen, string cuerpoHtml,
        IReadOnlyList<AdjuntoParaEnviarDto>? adjuntos, CancellationToken cancellationToken)
    {
        BuzonesUsados.Add(buzon);
        VecesEnviado++;
        UltimoMensajeExternoIdRespondido = mensajeExternoIdOrigen;
        UltimoCuerpoRespondido = cuerpoHtml;
        return Task.FromResult(FallaEnvio
            ? Result.Fallo(Error.Crear("Integraciones.Microsoft365.ErrorEnvio", "fallo simulado"))
            : Result.Exito());
    }

    public Task<Result<MensajeEnviadoGraphDto>> EnviarNuevoMensajeAsync(
        string accessToken, BuzonGraph buzon, IReadOnlyList<string> destinatarios, string asunto, string cuerpoHtml,
        IReadOnlyList<AdjuntoParaEnviarDto>? adjuntos, CancellationToken cancellationToken,
        IReadOnlyList<string>? copia = null)
    {
        BuzonesUsados.Add(buzon);
        VecesEnviado++;
        UltimaCopia = copia;
        return Task.FromResult(FallaEnvio
            ? Result.Fallo<MensajeEnviadoGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorEnvio", "fallo simulado"))
            : Result.Exito(MensajeEnviadoADevolver));
    }

    /// <summary>Las direcciones en copia con las que se llamó por última vez a <see cref="EnviarNuevoMensajeAsync"/>.</summary>
    public IReadOnlyList<string>? UltimaCopia { get; private set; }

    public Task<Result<MensajeGraphDto>> ObtenerMensajeAsync(
        string accessToken, BuzonGraph buzon, string mensajeId, CancellationToken cancellationToken) =>
        Registrar(buzon, (MensajesPorId.GetValueOrDefault(mensajeId) ?? MensajeADevolver) is not { } mensaje
            ? Result.Fallo<MensajeGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorApi", "sin mensaje configurado"))
            : Result.Exito(mensaje));

    public Task<Result<byte[]>> ObtenerContenidoAdjuntoAsync(
        string accessToken, BuzonGraph buzon, string mensajeId, string adjuntoExternoId, CancellationToken cancellationToken) =>
        Registrar(buzon, Result.Exito(ContenidoAdjuntoADevolver));

    public Task<Result<IReadOnlyList<CarpetaGraphDto>>> ListarCarpetasAsync(string accessToken, BuzonGraph buzon, CancellationToken cancellationToken) =>
        Registrar(buzon, Result.Exito(CarpetasADevolver));

    public Task<Result<IReadOnlyList<MensajeResumenGraphDto>>> ListarMensajesAsync(
        string accessToken, BuzonGraph buzon, string carpetaExternoId, int top, int skip, CancellationToken cancellationToken) =>
        Registrar(buzon, Result.Exito(MensajesADevolver));

    public Task<Result<SuscripcionGraphDto>> CrearSuscripcionAsync(
        string accessToken, BuzonGraph buzon, string notificationUrl, string clientState, CancellationToken cancellationToken)
    {
        VecesSuscrito++;
        return Registrar(buzon, FallaCreacionSuscripcion
            ? Result.Fallo<SuscripcionGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorApi", "fallo simulado"))
            : Result.Exito(new SuscripcionGraphDto("suscripcion-falsa", DateTime.UtcNow.AddDays(3))));
    }

    private Task<T> Registrar<T>(BuzonGraph buzon, T resultado)
    {
        BuzonesUsados.Add(buzon);
        return Task.FromResult(resultado);
    }

    public Task<Result<SuscripcionGraphDto>> RenovarSuscripcionAsync(string accessToken, string graphSubscriptionId, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Exito(new SuscripcionGraphDto(graphSubscriptionId, DateTime.UtcNow.AddDays(3))));

    public List<string> SuscripcionesEliminadas { get; } = [];

    public Task<Result> EliminarSuscripcionAsync(string accessToken, string graphSubscriptionId, CancellationToken cancellationToken)
    {
        SuscripcionesEliminadas.Add(graphSubscriptionId);
        return Task.FromResult(Result.Exito());
    }

    public IReadOnlyList<string> ExtraerMensajeIdsDeNotificacion(string payloadJson) => MensajeIdsADevolver;

    public string? ExtraerClientStateDeNotificacion(string payloadJson) => ClientStateADevolver;

    public string? ExtraerSubscriptionIdDeNotificacion(string payloadJson) => SubscriptionIdADevolver;
}
