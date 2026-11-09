using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;

namespace CaeManager.Application.Integraciones;

/// <summary>
/// <see cref="AccessTokenExpiraUtc"/> (auditoría módulo 6, ampliación de
/// AccesoGraphService): permite cachear el access token entre llamadas en
/// vez de refrescar en cada una — antes, cada operación rotaba el refresh
/// token aunque el access token vigente todavía sirviera, multiplicando sin
/// necesidad la ventana en la que dos refrescos concurrentes pueden
/// competir por la misma conexión.
///
/// <see cref="DirectorioEntraId"/> es el claim <c>tid</c> del token que devolvió el endpoint de tokens de Entra en
/// ese mismo canje (GUID no nulo), o null si no se pudo leer: el directorio de un buzón compartido sale de ahí y
/// nunca de nada que mande el navegador (ni <c>state</c> ni la query del callback).
/// </summary>
public record TokensGraphDto(string AccessToken, string RefreshToken, DateTime AccessTokenExpiraUtc, string? DirectorioEntraId = null);

/// <summary>Token de aplicación de un directorio Entra: sin refresh token, se pide otro al caducar.</summary>
public record TokenAplicacionGraphDto(string AccessToken, DateTime ExpiraUtc);

public record SuscripcionGraphDto(string GraphSubscriptionId, DateTime FechaExpiracionUtc);

public record ParticipanteGraphDto(string Email, RolParticipante Rol);

public record AdjuntoGraphDto(string AdjuntoExternoId, string NombreArchivo, string TipoContenido, long TamanoBytes);

/// <summary>Entrada para enviar un adjunto — el contenido va inline en base64 (límite de Graph sin sesión de subida, ver <see cref="IMicrosoft365GraphClient"/>).</summary>
public record AdjuntoParaEnviarDto(string NombreArchivo, string TipoContenido, byte[] Contenido);

public record MensajeGraphDto(
    string MensajeExternoId, string HiloExternoId, string Asunto, string Remitente, string CuerpoHtml, DateTime FechaUtc,
    IReadOnlyList<ParticipanteGraphDto> Participantes, IReadOnlyList<AdjuntoGraphDto> Adjuntos,
    IReadOnlyList<CabeceraCorreoDto>? Cabeceras = null, IReadOnlyList<string>? ResponderA = null);

public record CabeceraCorreoDto(string Nombre, string Valor);

/// <summary>Fila ligera para el historial de una carpeta — sin cuerpo, para que listar no cargue el HTML completo de cada mensaje.</summary>
public record MensajeResumenGraphDto(
    string MensajeExternoId, string HiloExternoId, string Asunto, string Remitente, DateTime FechaUtc, bool TieneAdjuntos);

/// <summary>Carpeta del buzón (Bandeja de entrada, Enviados, y las subcarpetas propias del cliente) — ver § "estructura de carpetas".</summary>
public record CarpetaGraphDto(
    string CarpetaExternoId, string Nombre, string? CarpetaPadreExternoId, int ElementosNoLeidos, int TotalElementos);

public record MensajeEnviadoGraphDto(string MensajeExternoId, string HiloExternoId);

/// <summary>
/// Buzón sobre el que actúa una llamada a Graph. El propio de quien consintió
/// (<see cref="EsCompartido"/> falso) va por <c>/me/...</c>, resuelto por el
/// token; uno compartido va por <c>/users/{dirección}/...</c> con el token de
/// aplicación de su directorio, y lo que puede abrir lo acota en Exchange el
/// ámbito de RBAC for Applications de ese directorio.
/// </summary>
public readonly record struct BuzonGraph(string Direccion, bool EsCompartido)
{
    public static BuzonGraph De(ConexionIntegracion conexion) => new(conexion.BuzonEmail, conexion.EsBuzonCompartido);
}

public static class LimitesAdjuntosCorreo
{
    /// <summary>
    /// Límite real de Microsoft Graph para adjuntos inline en sendMail/reply
    /// sin usar una sesión de subida (<c>createUploadSession</c>) — por
    /// encima de esto Graph rechaza la petición. No se implementa sesión de
    /// subida en este slice (YAGNI): un correo de gestión CAE con más de
    /// 3 MB de adjuntos es el caso raro, no el común. El llamador (Application)
    /// debe validar contra esto antes de invocar
    /// <see cref="IMicrosoft365GraphClient.EnviarRespuestaAsync"/>/<see cref="IMicrosoft365GraphClient.EnviarNuevoMensajeAsync"/>.
    /// </summary>
    public const long TamanoMaximoTotalAdjuntosBytes = 3 * 1024 * 1024;

    /// <summary>
    /// Tope defensivo para un adjunto ENTRANTE (mismo valor que
    /// <c>LimitesMediaWhatsApp.TamanoMaximoBytes</c>): el tamaño declarado por
    /// Graph antes de descargar es solo un pre-filtro — <see cref="IMicrosoft365GraphClient.ObtenerContenidoAdjuntoAsync"/>
    /// vuelve a acotar mientras copia los bytes reales, en vez de confiar
    /// solo en el metadato (auditoría módulo 6).
    /// </summary>
    public const long TamanoMaximoDescargaBytes = 25 * 1024 * 1024;
}

/// <summary>
/// El "proveedor de integración" de este slice (ver Project-Hydra-Negocio/tecnico/ARQUITECTURA-INTEGRACIONES.md
/// § 4) — nombrado por el proveedor concreto en vez de una abstracción
/// multi-proveedor genérica: con un único proveedor real (Microsoft 365) esa
/// generalidad es especulativa (YAGNI). Se generaliza a
/// <c>IIntegrationProvider</c> cuando exista un segundo proveedor confirmado.
///
/// La implementación real (Infrastructure) es la única que conoce el
/// formato de wire de Graph — <see cref="ExtraerMensajeIdsDeNotificacion"/>
/// incluido, aunque no haga ninguna llamada HTTP: el mapeo de campos de un
/// proveedor externo vive solo en su adaptador (Project-Hydra-Negocio/tecnico/docs/INTEGRATION_GUIDELINES.md
/// paso 6), nunca en Application.
///
/// <para>
/// Lo que se inyecta siempre lleva delante la guarda de envío (C-04):
/// <see cref="EnviarRespuestaAsync"/> y <see cref="EnviarNuevoMensajeAsync"/>
/// devuelven un fallo con <c>Email.EnvioCerrado</c> mientras la guarda no los
/// deje salir. Un método nuevo que saque correo tiene que pasar por ella.
/// </para>
/// </summary>
public interface IMicrosoft365GraphClient
{
    string ConstruirUrlAutorizacion(string redirectUri, string state);

    Task<Result<TokensGraphDto>> IntercambiarCodigoPorTokensAsync(string code, string redirectUri, CancellationToken cancellationToken);

    /// <summary>Graph puede rotar el refresh token en cada canje — el llamador debe persistir el que devuelve esta llamada, no reutilizar el anterior.</summary>
    Task<Result<TokensGraphDto>> RefrescarTokensAsync(string refreshToken, CancellationToken cancellationToken);

    /// <summary>
    /// Token de aplicación (client credentials) en el directorio Entra <paramref name="directorioId"/>, para los
    /// buzones compartidos. Lo que puede abrir lo decide el ámbito de RBAC for Applications que el administrador
    /// de Exchange de ese directorio asignó a la aplicación, no este token. Falla sin llamar a Entra si
    /// <paramref name="directorioId"/> no es un GUID.
    /// </summary>
    Task<Result<TokenAplicacionGraphDto>> ObtenerTokenAplicacionAsync(string directorioId, CancellationToken cancellationToken);

    Task<Result<string>> ObtenerBuzonEmailAsync(string accessToken, CancellationToken cancellationToken);

    /// <summary>
    /// Identidad estable del buzón: el Id inmutable de su Bandeja de entrada. Es la misma por
    /// cualquier dirección o alias que lo abra (correo principal, UPN, proxy) y por cualquier
    /// token (delegado o de aplicación), y por eso es la clave de la reclamación global del
    /// buzón, no la dirección escrita. Antes del alta de un compartido sirve además de sonda: si
    /// el token de aplicación del directorio no puede leer esa Bandeja, el buzón está fuera del
    /// ámbito de RBAC for Applications (Graph no lista ese ámbito).
    /// </summary>
    Task<Result<string>> ObtenerIdentidadBuzonAsync(string accessToken, BuzonGraph buzon, CancellationToken cancellationToken);

    /// <summary>
    /// Usa el endpoint /reply de Graph — preserva threading automáticamente,
    /// nunca reconstruye In-Reply-To/References a mano. <paramref name="adjuntos"/>
    /// va inline en base64 (sin sesión de subida) — respeta
    /// <see cref="LimitesAdjuntosCorreo.TamanoMaximoTotalAdjuntosBytes"/>
    /// del lado del llamador antes de invocar esto.
    /// </summary>
    Task<Result> EnviarRespuestaAsync(
        string accessToken, BuzonGraph buzon, string mensajeExternoIdOrigen, string cuerpoHtml,
        IReadOnlyList<AdjuntoParaEnviarDto>? adjuntos, CancellationToken cancellationToken);

    /// <summary>
    /// Redactar un mensaje nuevo (no una respuesta a un hilo existente) — borrador + envío, devuelve los Ids externos (mensaje e hilo).
    /// <paramref name="copia"/> son las direcciones en copia (<c>ccRecipients</c>); null o vacío = sin copia.
    /// <para>
    /// Son dos llamadas a Graph, y entre ellas la conexión puede pasar a ConError o desconectarse.
    /// <paramref name="puertaAntesDeEnviar"/> se evalúa después de crear el borrador y antes de enviarlo: el llamador
    /// relee ahí el estado de la conexión (Habilitada y con la identidad verificada). Si devuelve false o lanza, el
    /// borrador no se envía, se intenta borrar y el resultado es <see cref="ErroresBuzonSinVerificar"/>. Infrastructure
    /// no lee repositorios: la regla sigue en Application y aquí solo se respeta el orden.
    /// </para>
    /// </summary>
    Task<Result<MensajeEnviadoGraphDto>> EnviarNuevoMensajeAsync(
        string accessToken, BuzonGraph buzon, IReadOnlyList<string> destinatarios, string asunto, string cuerpoHtml,
        IReadOnlyList<AdjuntoParaEnviarDto>? adjuntos, Func<CancellationToken, Task<bool>> puertaAntesDeEnviar,
        CancellationToken cancellationToken, IReadOnlyList<string>? copia = null);

    /// <summary>Incluye los adjuntos del mensaje (metadatos, no el contenido — ver <see cref="ObtenerContenidoAdjuntoAsync"/>).</summary>
    Task<Result<MensajeGraphDto>> ObtenerMensajeAsync(
        string accessToken, BuzonGraph buzon, string mensajeId, CancellationToken cancellationToken);

    /// <summary>Contenido binario de un adjunto ya listado en <see cref="MensajeGraphDto.Adjuntos"/> — llamada aparte porque Graph no lo trae en el GET del mensaje.</summary>
    Task<Result<byte[]>> ObtenerContenidoAdjuntoAsync(
        string accessToken, BuzonGraph buzon, string mensajeId, string adjuntoExternoId, CancellationToken cancellationToken);

    /// <summary>
    /// Estructura de carpetas del buzón — dos niveles (carpetas de primer
    /// nivel + sus subcarpetas inmediatas), suficiente para el uso real de
    /// un buzón de gestión CAE (Bandeja de entrada/Enviados/Archivo con
    /// como mucho una subcarpeta propia) sin la complejidad de recorrer un
    /// árbol de profundidad arbitraria.
    /// </summary>
    /// <remarks>
    /// Son varias peticiones (la lista de primer nivel y una por cada carpeta para sus hijas). Entre una y otra la conexión
    /// puede pasar a ConError o desconectarse: <paramref name="puertaEntrePeticiones"/> se evalúa antes de cada petición
    /// de hijas, y si devuelve false o lanza, el listado se corta con <see cref="ErroresBuzonSinVerificar"/>.
    /// </remarks>
    Task<Result<IReadOnlyList<CarpetaGraphDto>>> ListarCarpetasAsync(
        string accessToken, BuzonGraph buzon, Func<CancellationToken, Task<bool>> puertaEntrePeticiones, CancellationToken cancellationToken);

    /// <summary>Historial de una carpeta (más recientes primero) — para consultar cadenas antiguas sin salir a Outlook.</summary>
    Task<Result<IReadOnlyList<MensajeResumenGraphDto>>> ListarMensajesAsync(
        string accessToken, BuzonGraph buzon, string carpetaExternoId, int top, int skip, CancellationToken cancellationToken);

    /// <summary>
    /// Suscribe la Bandeja de entrada del buzón: <c>me/...</c> con el token delegado
    /// del buzón propio, <c>users/{dirección}/...</c> con el token de aplicación
    /// del directorio para uno compartido (Graph no admite suscripciones delegadas
    /// sobre un buzón compartido).
    /// </summary>
    Task<Result<SuscripcionGraphDto>> CrearSuscripcionAsync(
        string accessToken, BuzonGraph buzon, string notificationUrl, string clientState, CancellationToken cancellationToken);

    Task<Result<SuscripcionGraphDto>> RenovarSuscripcionAsync(string accessToken, string graphSubscriptionId, CancellationToken cancellationToken);

    /// <summary>Best-effort — el llamador no debe bloquear una desconexión local si esto falla (la suscripción expira sola).</summary>
    Task<Result> EliminarSuscripcionAsync(string accessToken, string graphSubscriptionId, CancellationToken cancellationToken);

    /// <summary>Puro parseo del payload de notificación de Graph — sin llamada de red. Devuelve los Ids de mensaje a los que hay que ir a buscar el contenido real.</summary>
    IReadOnlyList<string> ExtraerMensajeIdsDeNotificacion(string payloadJson);

    /// <summary>
    /// Puro parseo, sin llamada de red — el <c>clientState</c> que Graph
    /// devuelve sin cambios en cada notificación (Project-Hydra-Negocio/tecnico/docs/MULTITENANCY.md § 8,
    /// tercer modo). El endpoint del webhook lo compara contra el secreto
    /// guardado en <c>SuscripcionWebhook</c> antes de confiar en nada más
    /// del payload. Null si el payload no trae ninguna notificación válida, o
    /// si alguna notificación del lote no trae el mismo <c>clientState</c> y
    /// el mismo <c>subscriptionId</c> que la primera: la ingesta procesa todos
    /// los mensajes del lote, así que ninguno puede colarse detrás de una
    /// primera notificación verificada.
    /// </summary>
    string? ExtraerClientStateDeNotificacion(string payloadJson);

    /// <summary>
    /// Puro parseo, sin llamada de red — el <c>subscriptionId</c> que Graph
    /// asigna a la suscripción y devuelve en cada notificación. Segunda
    /// comprobación además del <c>clientState</c> (auditoría módulo 6): si un
    /// <c>clientState</c> llegara a filtrarse, exigir también que coincida
    /// con la suscripción activa de <c>SuscripcionWebhook</c> reduce lo que
    /// un payload fabricado puede hacer aceptar. Null en los mismos casos que
    /// <see cref="ExtraerClientStateDeNotificacion"/>, lote heterogéneo incluido.
    /// </summary>
    string? ExtraerSubscriptionIdDeNotificacion(string payloadJson);
}

/// <summary>Códigos de error de <see cref="IMicrosoft365GraphClient"/> que un llamador trata de forma distinta al resto.</summary>
public static class CodigosErrorMicrosoft365
{
    /// <summary>
    /// Graph respondió 404 con <c>ErrorItemNotFound</c> al leer un mensaje: el buzón existe y el mensaje ya no. Cualquier
    /// otro fallo de lectura es transitorio, de permiso o <see cref="BuzonNoEncontrado"/>.
    /// </summary>
    public const string MensajeNoEncontrado = "Integraciones.Microsoft365.MensajeNoEncontrado";

    /// <summary>
    /// Graph respondió 404 al leer un mensaje sin decir que lo que falta es el mensaje (<c>ResourceNotFound</c>,
    /// <c>MailboxNotEnabledForRESTAPI</c>, <c>ErrorInvalidUser</c>, un cuerpo ilegible...): puede ser el buzón el que ya
    /// no existe. No resuelve el evento.
    /// </summary>
    public const string BuzonNoEncontrado = "Integraciones.Microsoft365.BuzonNoEncontrado";

    /// <summary>El buzón está fuera del alcance del token (403/404 en su Bandeja de entrada).</summary>
    public const string SinAccesoBuzon = "Integraciones.Microsoft365.SinAccesoBuzon";

    /// <summary>Graph rechazó con 403 la creación de un borrador: al token le falta Mail.ReadWrite sobre ese buzón.</summary>
    public const string SinPermisoEscritura = "Integraciones.Microsoft365.SinPermisoEscritura";

    /// <summary>
    /// Microsoft no rechazó nada, solo no pudo atender ahora: un 408, 429 o 5xx al pedir un token o renovar una
    /// suscripción, o un 200 con un cuerpo de tokens ilegible.
    /// </summary>
    public const string Transitorio = "Integraciones.Microsoft365.Transitorio";

    /// <summary>Fallo de red: la petición no llegó a Microsoft o no volvió respuesta.</summary>
    public const string ErrorRed = "Integraciones.Microsoft365.ErrorRed";

    /// <summary>Microsoft no respondió a tiempo (timeout del cliente HTTP, no una cancelación pedida).</summary>
    public const string TiempoAgotado = "Integraciones.Microsoft365.TiempoAgotado";

    /// <summary>
    /// Un fallo que no dice nada de la conexión: se reintenta en el siguiente ciclo y no la pasa a ConError. El resto
    /// (4xx de Entra o de Graph, directorio inválido, permiso sin acotar, sin credencial o sin certificado) es definitivo.
    /// </summary>
    public static bool EsTransitorio(Error error) => error.Codigo is Transitorio or ErrorRed or TiempoAgotado;
}
