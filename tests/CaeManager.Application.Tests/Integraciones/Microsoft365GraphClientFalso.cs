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
        Task.FromResult(Result.Exito(new TokensGraphDto(AccessTokenDevuelto, RefreshTokenDevuelto, AccessTokenExpiraUtcDevuelto, DirectorioDevuelto)));

    /// <summary>El claim «tid» del token que devuelve el canje; null, un token sin directorio legible.</summary>
    public string? DirectorioDevuelto { get; set; } = "11111111-2222-3333-4444-555555555555";

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
        return Task.FromResult(FallaTokenAplicacion
            ? Result.Fallo<TokenAplicacionGraphDto>(Error.Crear("Integraciones.Microsoft365.PermisoSinAcotar", "fallo simulado"))
            : Result.Exito(new TokenAplicacionGraphDto("token-aplicacion-falso", DateTime.UtcNow.AddHours(1))));
    }

    /// <summary>El directorio no da token de aplicación (sin consentimiento o con permiso de correo sin acotar).</summary>
    public bool FallaTokenAplicacion { get; set; }

    /// <summary>Dirección del buzón propio que devuelve <c>/me</c>.</summary>
    public string BuzonEmailPropio { get; set; } = "buzon@cliente-falso.test";

    public Task<Result<string>> ObtenerBuzonEmailAsync(string accessToken, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Exito(BuzonEmailPropio));

    /// <summary>Cada buzón sobre el que se pidió algo a Graph, en orden: prueba que una conexión compartida no cae en /me.</summary>
    public List<BuzonGraph> BuzonesUsados { get; } = [];

    public int VecesSuscrito { get; private set; }

    /// <summary>
    /// Identidad de cada buzón por su dirección en minúsculas; sin entrada, la propia dirección. Dos alias del
    /// mismo buzón se simulan dándoles la misma identidad.
    /// </summary>
    public Dictionary<string, string> IdentidadesPorDireccion { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Identidad del buzón propio (<c>/me</c>), que no tiene dirección en la ruta.</summary>
    public string IdentidadBuzonPropio { get; set; } = "inbox-propio";

    /// <summary>Error que devuelve la sonda de identidad; null, éxito.</summary>
    public Error? ErrorIdentidadBuzon { get; set; }

    /// <summary>Direcciones de compartido cuya sonda de identidad lanza una excepción en vez de devolver un resultado.</summary>
    public HashSet<string> DireccionesQueLanzanEnIdentidad { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tokens con los que se pidió la identidad, en orden: prueba que la sonda del compartido usa el de aplicación.</summary>
    public List<string> TokensDeIdentidad { get; } = [];

    /// <summary>Se ejecuta durante la lectura de la identidad: simula lo que pasa mientras dura la llamada a Graph.</summary>
    public Action? AlLeerIdentidad { get; set; }

    public Task<Result<string>> ObtenerIdentidadBuzonAsync(string accessToken, BuzonGraph buzon, CancellationToken cancellationToken)
    {
        AlLeerIdentidad?.Invoke();
        BuzonesUsados.Add(buzon);
        TokensDeIdentidad.Add(accessToken);
        if (buzon.EsCompartido && DireccionesQueLanzanEnIdentidad.Contains(buzon.Direccion))
            throw new InvalidOperationException("fallo inesperado simulado");
        if (ErrorIdentidadBuzon is { } error)
            return Task.FromResult(Result.Fallo<string>(error));

        var identidad = !buzon.EsCompartido
            ? IdentidadBuzonPropio
            : IdentidadesPorDireccion.GetValueOrDefault(buzon.Direccion) ?? $"inbox-{buzon.Direccion.ToLowerInvariant()}";
        return Task.FromResult(Result.Exito(identidad));
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

    /// <summary>
    /// Como el cliente real: crea el borrador, evalúa la puerta del llamador y solo si la pasa lo envía. Un borrador
    /// que no pasa la puerta no cuenta como enviado y queda en <see cref="BorradoresNoEnviados"/>.
    /// </summary>
    public async Task<Result<MensajeEnviadoGraphDto>> EnviarNuevoMensajeAsync(
        string accessToken, BuzonGraph buzon, IReadOnlyList<string> destinatarios, string asunto, string cuerpoHtml,
        IReadOnlyList<AdjuntoParaEnviarDto>? adjuntos, Func<CancellationToken, Task<bool>> puertaAntesDeEnviar,
        CancellationToken cancellationToken, IReadOnlyList<string>? copia = null)
    {
        BuzonesUsados.Add(buzon);
        BorradoresCreados++;
        if (!await puertaAntesDeEnviar(cancellationToken))
        {
            BorradoresNoEnviados++;
            return Result.Fallo<MensajeEnviadoGraphDto>(ErroresBuzonSinVerificar.Crear());
        }

        VecesEnviado++;
        UltimaCopia = copia;
        return FallaEnvio
            ? Result.Fallo<MensajeEnviadoGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorEnvio", "fallo simulado"))
            : Result.Exito(MensajeEnviadoADevolver);
    }

    /// <summary>Las direcciones en copia con las que se llamó por última vez a <see cref="EnviarNuevoMensajeAsync"/>.</summary>
    public IReadOnlyList<string>? UltimaCopia { get; private set; }

    /// <summary>Borradores de mensaje nuevo creados, se enviaran o no.</summary>
    public int BorradoresCreados { get; private set; }

    /// <summary>Borradores creados que no se enviaron porque la puerta del llamador dijo que no.</summary>
    public int BorradoresNoEnviados { get; private set; }

    public Task<Result<MensajeGraphDto>> ObtenerMensajeAsync(
        string accessToken, BuzonGraph buzon, string mensajeId, CancellationToken cancellationToken) =>
        Registrar(buzon, (ErroresLecturaPorId.GetValueOrDefault(mensajeId) ?? ErrorLecturaMensaje) is { } error
            ? Result.Fallo<MensajeGraphDto>(error)
            : (MensajesPorId.GetValueOrDefault(mensajeId) ?? MensajeADevolver) is not { } mensaje
                // Sin mensaje configurado: el 404 de Graph, un mensaje borrado entre la notificación y la lectura.
                ? Result.Fallo<MensajeGraphDto>(Error.Crear(CodigosErrorMicrosoft365.MensajeNoEncontrado, "sin mensaje configurado"))
                : Result.Exito(mensaje));

    /// <summary>Fallo de lectura que no es un 404 (403, 429, 5xx, timeout); null, el comportamiento normal.</summary>
    public Error? ErrorLecturaMensaje { get; set; }

    /// <summary>Fallo de lectura que no es un 404, solo para el mensaje con ese Id.</summary>
    public Dictionary<string, Error> ErroresLecturaPorId { get; } = [];

    public Task<Result<byte[]>> ObtenerContenidoAdjuntoAsync(
        string accessToken, BuzonGraph buzon, string mensajeId, string adjuntoExternoId, CancellationToken cancellationToken) =>
        Registrar(buzon, Result.Exito(ContenidoAdjuntoADevolver));

    /// <summary>Como el cliente real: la puerta se evalúa una vez por carpeta, antes de pedir sus hijas.</summary>
    public async Task<Result<IReadOnlyList<CarpetaGraphDto>>> ListarCarpetasAsync(
        string accessToken, BuzonGraph buzon, Func<CancellationToken, Task<bool>> puertaEntrePeticiones, CancellationToken cancellationToken)
    {
        foreach (var _ in CarpetasADevolver)
        {
            if (!await puertaEntrePeticiones(cancellationToken))
                return await Registrar(buzon, Result.Fallo<IReadOnlyList<CarpetaGraphDto>>(ErroresBuzonSinVerificar.Crear()));
        }

        return await Registrar(buzon, Result.Exito(CarpetasADevolver));
    }

    public Task<Result<IReadOnlyList<MensajeResumenGraphDto>>> ListarMensajesAsync(
        string accessToken, BuzonGraph buzon, string carpetaExternoId, int top, int skip, CancellationToken cancellationToken)
    {
        ListadosPedidos.Add((carpetaExternoId, top, skip));
        return Registrar(buzon, ErrorListado is { } error
            ? Result.Fallo<IReadOnlyList<MensajeResumenGraphDto>>(error)
            : Result.Exito<IReadOnlyList<MensajeResumenGraphDto>>(MensajesADevolver.Skip(skip).Take(top).ToList()));
    }

    /// <summary>Cada página pedida a <see cref="ListarMensajesAsync"/>: carpeta, tamaño y desplazamiento.</summary>
    public List<(string Carpeta, int Top, int Skip)> ListadosPedidos { get; } = [];

    /// <summary>Fallo del listado de una carpeta; null, éxito.</summary>
    public Error? ErrorListado { get; set; }

    public Task<Result<SuscripcionGraphDto>> CrearSuscripcionAsync(
        string accessToken, BuzonGraph buzon, string notificationUrl, string clientState, CancellationToken cancellationToken)
    {
        VecesSuscrito++;
        ClientStatesSuscritos.Add(clientState);
        AlCrearSuscripcion?.Invoke();
        if (ExcepcionCreacionSuscripcion is { } excepcion)
            throw excepcion;

        return Registrar(buzon, ErrorCreacionSuscripcion is { } errorCreacion
            ? Result.Fallo<SuscripcionGraphDto>(errorCreacion)
            : FallaCreacionSuscripcion
            ? Result.Fallo<SuscripcionGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorApi", "fallo simulado"))
            : Result.Exito(new SuscripcionGraphDto("suscripcion-falsa", DateTime.UtcNow.AddDays(3))));
    }

    /// <summary>Se invoca al pedir la creación de una suscripción, antes de responder (p. ej. para ver qué estaba guardado ya).</summary>
    public Action? AlCrearSuscripcion { get; set; }

    /// <summary>Excepción que lanza la creación de suscripción (p. ej. una cancelación del POST); null, no lanza.</summary>
    public Exception? ExcepcionCreacionSuscripcion { get; set; }

    /// <summary>Fallo concreto de la creación de suscripción (p. ej. transitorio); null, el de <see cref="FallaCreacionSuscripcion"/>.</summary>
    public Error? ErrorCreacionSuscripcion { get; set; }

    /// <summary>El clientState de cada suscripción creada.</summary>
    public List<string> ClientStatesSuscritos { get; } = [];

    /// <summary>Base de la URL del webhook; null simula la falta de URL pública.</summary>
    public string? UrlPublicaBase { get; set; } = "https://app.operador.test";

    public string? UrlNotificacion(Guid conexionId) =>
        UrlPublicaBase is null ? null : $"{UrlPublicaBase}/api/integraciones/webhooks/microsoft365/{conexionId}";

    private Task<T> Registrar<T>(BuzonGraph buzon, T resultado)
    {
        BuzonesUsados.Add(buzon);
        return Task.FromResult(resultado);
    }

    public Task<Result<SuscripcionGraphDto>> RenovarSuscripcionAsync(string accessToken, string graphSubscriptionId, CancellationToken cancellationToken)
    {
        SuscripcionesRenovadas.Add(graphSubscriptionId);
        AlRenovarSuscripcion?.Invoke(graphSubscriptionId);
        return Task.FromResult(ErroresRenovacion.TryGetValue(graphSubscriptionId, out var error)
            ? Result.Fallo<SuscripcionGraphDto>(error)
            : Result.Exito(new SuscripcionGraphDto(graphSubscriptionId, DateTime.UtcNow.AddDays(3))));
    }

    /// <summary>
    /// Se invoca durante la renovación, antes de responder: simula lo que pasa mientras dura la petición (que la
    /// suscripción expire) o fotografía qué estaba guardado ya cuando salió la llamada.
    /// </summary>
    public Action<string>? AlRenovarSuscripcion { get; set; }

    /// <summary>Cada renovación pedida a Graph, por id de suscripción.</summary>
    public List<string> SuscripcionesRenovadas { get; } = [];

    /// <summary>Renovaciones que fallan, por id de suscripción; las demás renuevan tres días.</summary>
    public Dictionary<string, Error> ErroresRenovacion { get; } = [];

    public List<string> SuscripcionesEliminadas { get; } = [];

    public Task<Result> EliminarSuscripcionAsync(string accessToken, string graphSubscriptionId, CancellationToken cancellationToken)
    {
        SuscripcionesEliminadas.Add(graphSubscriptionId);
        AlEliminarSuscripcion?.Invoke();
        if (ExcepcionEliminacion is { } excepcion)
            throw excepcion;

        return Task.FromResult(ErrorEliminacion is { } error ? Result.Fallo(error) : Result.Exito());
    }

    /// <summary>Excepción que lanza el borrado de suscripción (resultado incierto); null, no lanza.</summary>
    public Exception? ExcepcionEliminacion { get; set; }

    /// <summary>Se invoca al pedir el borrado de una suscripción (p. ej. para ver qué estaba guardado ya).</summary>
    public Action? AlEliminarSuscripcion { get; set; }

    /// <summary>Fallo del borrado de suscripción; null, éxito.</summary>
    public Error? ErrorEliminacion { get; set; }

    public IReadOnlyList<string> ExtraerMensajeIdsDeNotificacion(string payloadJson) => MensajeIdsADevolver;

    public string? ExtraerClientStateDeNotificacion(string payloadJson) => ClientStateADevolver;

    public string? ExtraerSubscriptionIdDeNotificacion(string payloadJson) => SubscriptionIdADevolver;
}
