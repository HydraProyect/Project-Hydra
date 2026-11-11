using System.Net;
using System.Security.Cryptography;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Common;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.AltaAutonoma;

/// <summary>
/// Acceso a <c>SolicitudesAlta</c> desde el registro anónimo. La tabla está cerrada a los roles de la aplicación
/// (A1): la implementación llama a funciones <c>SECURITY DEFINER</c> acotadas, una por operación, y ninguna devuelve
/// la Solicitud de alta.
/// </summary>
public interface IAccesoSolicitudesAlta
{
    /// <summary>Guarda una Solicitud de alta recién iniciada (<see cref="SolicitudAlta.Iniciar"/>).</summary>
    Task IniciarAsync(SolicitudAlta solicitud, CancellationToken cancellationToken);

    /// <summary>
    /// Mira el enlace sin gastarlo: si vale (Solicitud de alta en <c>Iniciada</c>, enlace y Solicitud vigentes), devuelve
    /// el hash de la contraseña elegida en el registro para que la aplicación la compare con la que se escribe al
    /// confirmar (pre-secuestro de la cuenta, A4). El hash no sale de la aplicación hacia el navegador.
    /// </summary>
    Task<EnlaceVerificacionAlta> ConsultarEnlaceAsync(string token, CancellationToken cancellationToken);

    /// <summary>
    /// Iniciada → CorreoVerificado con el token del enlace, con el reloj de la base. Un solo uso. Exige que el hash de la
    /// contraseña siga siendo el que la aplicación comprobó y guarda el SHA-256 del credencial de continuación.
    /// </summary>
    Task<ResultadoVerificacionCorreoAlta> VerificarCorreoAsync(
        string token, string hashContrasenaComprobado, string credencialContinuacion, CancellationToken cancellationToken);

    /// <summary>
    /// CorreoVerificado → DatosCompletos o DerivadaAAcceso según el CIF tenga o no Tenant propietario (A4). Con el credencial
    /// de continuación; responde solo el resultado y el Id de la propia Solicitud de alta, nunca nada del Tenant.
    /// </summary>
    Task<TransicionDatosOrganizacionAlta> CompletarDatosOrganizacionAsync(
        string credencialContinuacion, DatosOrganizacionAlta datos, CancellationToken cancellationToken);

    /// <summary>
    /// En qué punto está la Solicitud de alta del credencial de continuación, sin bloquear ni escribir. Solo el estado,
    /// nunca un dato de la Solicitud de alta ni del Tenant.
    /// </summary>
    Task<EstadoContinuacionAlta> ConsultarContinuacionAsync(string credencialContinuacion, CancellationToken cancellationToken);
}

/// <summary>
/// Lo que dice la base de un enlace de verificación antes de gastarlo. <see cref="Rechazo"/> nulo significa que vale, y
/// entonces <see cref="HashContrasena"/> lleva el hash de Identity; con rechazo, el hash es siempre nulo.
/// </summary>
public sealed record EnlaceVerificacionAlta(ResultadoVerificacionCorreoAlta? Rechazo, string? HashContrasena)
{
    public static EnlaceVerificacionAlta NoValido { get; } = new(ResultadoVerificacionCorreoAlta.NoValido, null);
}

/// <summary>Resultado de confirmar el correo; con <see cref="ResultadoVerificacionCorreoAlta.Verificado"/>, el credencial de continuación en claro.</summary>
public sealed record VerificacionCorreoAlta(ResultadoVerificacionCorreoAlta Resultado, string? CredencialContinuacion);

/// <summary>¿Hay ya una cuenta con ese correo normalizado, en cualquier Tenant? Solo sí o no.</summary>
public interface IExistenciaCuentaPorCorreo
{
    Task<bool> ExisteAsync(string correoNormalizado, CancellationToken cancellationToken);
}

/// <summary>
/// Remitente de los correos del alta autónoma. En <c>Development</c> escribe a fichero y nunca sale por SMTP; en el
/// resto de entornos va por el canal de correo de la aplicación, detrás de su guarda de envío.
/// </summary>
public interface IRemitenteCorreoAlta
{
    Task<Result> EnviarAsync(string destinatario, string asunto, string cuerpoHtml, CancellationToken cancellationToken);
}

/// <summary>
/// Contraseña del registro: la comprueba con la política de contraseñas de Identity de la aplicación y la devuelve
/// como hash. La contraseña en claro no sale de la petición.
/// </summary>
public interface IPreparacionContrasenaAlta
{
    /// <returns>El hash de Identity, o <c>null</c> si la contraseña no cumple la política.</returns>
    Task<string?> ValidarYHashearAsync(string correo, string contrasena);

    /// <summary>¿La contraseña casa con el hash de Identity guardado en la Solicitud de alta?</summary>
    bool Coincide(string hashContrasena, string contrasena);
}

public enum ResultadoVerificacionCorreoAlta
{
    Verificado,

    /// <summary>El enlace pasó sus 24 horas; la Solicitud de alta sigue vigente.</summary>
    EnlaceCaducado,

    /// <summary>La Solicitud de alta pasó su plazo (DA-7).</summary>
    SolicitudCaducada,

    /// <summary>
    /// Token desconocido, ya usado o de una Solicitud de alta que no está en Iniciada, o contraseña que no casa con la
    /// del registro (A4): las dos cosas se responden igual, y una contraseña equivocada no gasta el enlace.
    /// </summary>
    NoValido,
}

/// <summary>Lo que el formulario del registro entrega, ya validado. La contraseña llega solo como hash.</summary>
public sealed record DatosRegistroAlta(string Correo, string HashContrasena, string Ip, string? UserAgent);

/// <summary>
/// Registro del alta autónoma (A3): abre una Solicitud de alta en <see cref="EstadoSolicitudAlta.Iniciada"/> y envía
/// el enlace de verificación, o, si el correo ya tiene cuenta, envía un aviso distinto sin crear nada (§ 3.4, RQ-28).
///
/// <para>
/// No es un <c>ICommand</c> de MediatR: quien registra es anónimo y <c>AutorizacionEscrituraBehavior</c> no tiene
/// ninguna vía para un Command sin usuario; abrirla sería un cambio de autorización. Lo invoca la cola en segundo
/// plano del registro, igual que <c>RecuperacionContrasena</c>, de modo que la respuesta al navegador no espera ni la
/// búsqueda de la cuenta ni el envío: misma respuesta y mismo tiempo exista o no el correo.
/// </para>
/// </summary>
public sealed class ServicioRegistroAlta(
    IAccesoSolicitudesAlta acceso,
    IExistenciaCuentaPorCorreo cuentas,
    IRemitenteCorreoAlta remitente,
    IPreparacionContrasenaAlta contrasenas,
    TimeProvider reloj,
    ILogger<ServicioRegistroAlta> logger)
{
    /// <summary>Longitud de un token o credencial de <see cref="NuevoToken"/>: 32 bytes en Base64url sin relleno.</summary>
    public const int LongitudToken = 43;

    /// <summary>Ruta pública del enlace de verificación.</summary>
    public const string RutaVerificacion = "alta/verificar";

    /// <summary>Bytes aleatorios del token: 256 bits, en Base64url (43 caracteres).</summary>
    private const int BytesToken = 32;

    public async Task IniciarAsync(DatosRegistroAlta datos, Uri basePublica, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(datos);
        ArgumentNullException.ThrowIfNull(basePublica);

        var correoNormalizado = SolicitudAlta.NormalizarCorreo(datos.Correo.Trim());
        if (await cuentas.ExisteAsync(correoNormalizado, cancellationToken))
        {
            var avisoCuenta = await remitente.EnviarAsync(datos.Correo.Trim(), AsuntoCuentaExistente,
                CuerpoCuentaExistente(basePublica), cancellationToken);
            if (avisoCuenta.EsFallido)
                logger.LogWarning("No se pudo enviar el aviso de cuenta existente del alta autónoma.");
            return;
        }

        var token = NuevoToken();
        var solicitud = SolicitudAlta.Iniciar(datos.Correo, datos.HashContrasena, token, datos.Ip, datos.UserAgent,
            reloj.GetUtcNow().UtcDateTime);
        await acceso.IniciarAsync(solicitud, cancellationToken);

        var enlace = new Uri(basePublica, $"{RutaVerificacion}?token={token}");
        var envio = await remitente.EnviarAsync(solicitud.Correo!, AsuntoVerificacion, CuerpoVerificacion(enlace), cancellationToken);
        if (envio.EsFallido)
            logger.LogWarning("No se pudo enviar el enlace de verificación de la Solicitud de alta {SolicitudAltaId}.", solicitud.Id);
    }

    /// <summary>
    /// Confirma el correo con el token del enlace y la contraseña elegida en el registro (pre-secuestro de la cuenta, § 9
    /// del plan): quien inició el alta con el correo de otra persona no conoce la contraseña que esa persona escribiría,
    /// y quien abre el enlace no conoce la del atacante. Solo verifica si casan los dos. Una contraseña equivocada
    /// responde lo mismo que un enlace no válido y no gasta el enlace: se puede reintentar, con el techo por IP del
    /// limitador global (<c>LimitadorAlta.ParticionVerificar</c>). Verificado, devuelve el credencial de continuación
    /// en claro, que la base guarda solo como SHA-256.
    /// </summary>
    public async Task<VerificacionCorreoAlta> VerificarCorreoAsync(string? token, string? contrasena, CancellationToken cancellationToken)
    {
        var noValido = new VerificacionCorreoAlta(ResultadoVerificacionCorreoAlta.NoValido, null);
        if (string.IsNullOrEmpty(token) || token.Length < SolicitudAlta.LongitudMinimaToken || token.Length > 128)
            return noValido;

        var enlace = await acceso.ConsultarEnlaceAsync(token, cancellationToken);
        if (enlace.Rechazo is { } rechazo)
            return new VerificacionCorreoAlta(rechazo, null);

        if (enlace.HashContrasena is null || string.IsNullOrEmpty(contrasena) || !contrasenas.Coincide(enlace.HashContrasena, contrasena))
            return noValido;

        var credencial = NuevoToken();
        var resultado = await acceso.VerificarCorreoAsync(token, enlace.HashContrasena, credencial, cancellationToken);
        return new VerificacionCorreoAlta(resultado, resultado == ResultadoVerificacionCorreoAlta.Verificado ? credencial : null);
    }

    /// <summary>Token de un solo uso en Base64url: solo letras, dígitos, «-» y «_», sin escapar en la URL.</summary>
    public static string NuevoToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(BytesToken)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public const string AsuntoVerificacion = "Confirma tu correo para crear tu cuenta en TALVEG";
    public const string AsuntoCuentaExistente = "Ya tienes una cuenta en TALVEG";

    private static string CuerpoVerificacion(Uri enlace) => $"""
        <p>Hemos recibido una solicitud para crear una cuenta en TALVEG con este correo.</p>
        <p><a href="{WebUtility.HtmlEncode(enlace.AbsoluteUri)}">Confirmar mi correo</a></p>
        <p>El enlace caduca a las 24 horas y solo vale una vez. Si no has sido tú, ignora este mensaje: sin confirmar el correo no se crea nada.</p>
        """;

    private static string CuerpoCuentaExistente(Uri basePublica) => $"""
        <p>Alguien ha intentado crear una cuenta en TALVEG con este correo, pero ya tienes una.</p>
        <p><a href="{WebUtility.HtmlEncode(new Uri(basePublica, "cuenta/iniciar-sesion").AbsoluteUri)}">Iniciar sesión</a>
        o, si no recuerdas la contraseña, <a href="{WebUtility.HtmlEncode(new Uri(basePublica, "cuenta/olvide-contrasena").AbsoluteUri)}">restablecerla</a>.</p>
        <p>Si no has sido tú, ignora este mensaje: tu cuenta no ha cambiado.</p>
        """;
}
