using System.Security.Cryptography;
using System.Text;

namespace CaeManager.Application.Seguridad.InicioSesion;

/// <summary>
/// Huella y descripción del dispositivo de un inicio de sesión.
///
/// <para>
/// <b>Huella.</b> Resumen SHA-256 (32 hex) del identificador aleatorio de la
/// cookie de dispositivo: dos inicios con la misma huella vienen del mismo
/// navegador. No se usa el User-Agent como huella porque «Chrome en Windows»
/// lo comparte media plantilla y un atacante con ese navegador no se vería
/// nunca como «dispositivo nuevo». La cookie es estrictamente necesaria para
/// la seguridad que pide la propia persona al iniciar sesión.
/// </para>
///
/// <para>
/// <b>Descripción.</b> Familia de navegador y de sistema, sin versión: es lo
/// que se enseña a la persona en el aviso, y lo único del User-Agent que se
/// guarda.
/// </para>
/// </summary>
public static class DispositivoDeInicioSesion
{
    public static string Huella(string identificadorDispositivo)
    {
        var resumen = SHA256.HashData(Encoding.UTF8.GetBytes(identificadorDispositivo));
        return Convert.ToHexString(resumen, 0, 16).ToLowerInvariant();
    }

    public static string NuevoIdentificador()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');

    /// <summary>¿Parece un identificador emitido por <see cref="NuevoIdentificador"/>? Una cookie manipulada se sustituye.</summary>
    public static bool EsIdentificadorValido(string? valor)
        => valor is { Length: 24 } && valor.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static string Describir(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return "Navegador desconocido";

        var ua = userAgent;
        var navegador =
            ua.Contains("Edg/", StringComparison.Ordinal) ? "Edge"
            : ua.Contains("OPR/", StringComparison.Ordinal) ? "Opera"
            : ua.Contains("Firefox/", StringComparison.Ordinal) ? "Firefox"
            : ua.Contains("Chrome/", StringComparison.Ordinal) || ua.Contains("CriOS/", StringComparison.Ordinal) ? "Chrome"
            : ua.Contains("Safari/", StringComparison.Ordinal) ? "Safari"
            : "Otro navegador";

        var sistema =
            ua.Contains("Windows", StringComparison.Ordinal) ? "Windows"
            : ua.Contains("Android", StringComparison.Ordinal) ? "Android"
            : ua.Contains("iPhone", StringComparison.Ordinal) || ua.Contains("iPad", StringComparison.Ordinal) ? "iOS"
            : ua.Contains("Mac OS X", StringComparison.Ordinal) || ua.Contains("Macintosh", StringComparison.Ordinal) ? "macOS"
            : ua.Contains("Linux", StringComparison.Ordinal) ? "Linux"
            : "otro sistema";

        return $"{navegador} · {sistema}";
    }
}
