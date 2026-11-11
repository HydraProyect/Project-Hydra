using System.Net;
using System.Security.Cryptography;
using CaeManager.Application.Common;
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

    /// <summary>Iniciada → CorreoVerificado con el token del enlace, con el reloj de la base. Un solo uso.</summary>
    Task<ResultadoVerificacionCorreoAlta> VerificarCorreoAsync(string token, CancellationToken cancellationToken);
}

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
}

public enum ResultadoVerificacionCorreoAlta
{
    Verificado,

    /// <summary>El enlace pasó sus 24 horas; la Solicitud de alta sigue vigente.</summary>
    EnlaceCaducado,

    /// <summary>La Solicitud de alta pasó su plazo (DA-7).</summary>
    SolicitudCaducada,

    /// <summary>Token desconocido, ya usado o de una Solicitud de alta que no está en Iniciada.</summary>
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
    TimeProvider reloj,
    ILogger<ServicioRegistroAlta> logger)
{
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

    public Task<ResultadoVerificacionCorreoAlta> VerificarCorreoAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token) || token.Length < SolicitudAlta.LongitudMinimaToken || token.Length > 128)
            return Task.FromResult(ResultadoVerificacionCorreoAlta.NoValido);

        return acceso.VerificarCorreoAsync(token, cancellationToken);
    }

    /// <summary>Token de un solo uso en Base64url: solo letras, dígitos, «-» y «_», sin escapar en la URL.</summary>
    public static string NuevoToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(BytesToken)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public const string AsuntoVerificacion = "Confirma tu correo para crear tu cuenta en TALVEG";
    public const string AsuntoCuentaExistente = "Ya tienes una cuenta en TALVEG";

    private static string CuerpoVerificacion(Uri enlace) => $"""
        <p>Hemos recibido una solicitud para crear una cuenta en TALVEG con este correo.</p>
        {BotonCorreo.Html("Confirmar mi correo", enlace.AbsoluteUri)}
        <p>El enlace caduca a las 24 horas y solo vale una vez. Si no has sido tú, ignora este mensaje: sin confirmar el correo no se crea nada.</p>
        """;

    private static string CuerpoCuentaExistente(Uri basePublica) => $"""
        <p>Alguien ha intentado crear una cuenta en TALVEG con este correo, pero ya tienes una.</p>
        {BotonCorreo.Html("Iniciar sesión", new Uri(basePublica, "cuenta/iniciar-sesion").AbsoluteUri)}
        <p>Si no recuerdas la contraseña, puedes restablecerla en <a href="{WebUtility.HtmlEncode(new Uri(basePublica, "cuenta/olvide-contrasena").AbsoluteUri)}" style="color:#122A21;text-decoration:underline;word-break:break-all;">{WebUtility.HtmlEncode(new Uri(basePublica, "cuenta/olvide-contrasena").AbsoluteUri)}</a>.</p>
        <p>Si no has sido tú, ignora este mensaje: tu cuenta no ha cambiado.</p>
        """;
}
