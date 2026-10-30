using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using CaeManager.Application.Common;
using QRCoder;

namespace CaeManager.Web.Components.Account;

/// <summary>
/// Cómo se enseña la clave del TOTP en las dos altas: la de la página de la 2FA, con sesión,
/// y la previa a la primera vinculación de Microsoft, en <c>LoginCon2fa</c>.
/// </summary>
public static class ClaveAutenticadorTotp
{
    /// <summary>
    /// Agrupa la clave de 4 en 4 caracteres para que sea más fácil de leer
    /// y transcribir a mano — mismo formato que usa la plantilla oficial de
    /// ASP.NET Core Identity para esta misma pantalla.
    /// </summary>
    public static string FormatearClave(string claveSinFormato)
    {
        var resultado = new StringBuilder();
        var posicion = 0;
        while (posicion + 4 < claveSinFormato.Length)
        {
            resultado.Append(claveSinFormato.AsSpan(posicion, 4)).Append(' ');
            posicion += 4;
        }
        if (posicion < claveSinFormato.Length)
            resultado.Append(claveSinFormato.AsSpan(posicion));

        return resultado.ToString().ToLowerInvariant();
    }

    // Marca.Nombre se fija desde la configuración al arrancar, así que se lee al usarse y no
    // al cargar el tipo.
    public static string GenerarUriOtpAuth(string email, string claveSinFormato) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "otpauth://totp/{0}:{1}?secret={2}&issuer={0}&digits=6",
            UrlEncoder.Default.Encode(Marca.Nombre),
            UrlEncoder.Default.Encode(email),
            claveSinFormato);

    /// <summary>
    /// PNG generado en el propio proceso (PngByteQRCode, sin System.Drawing
    /// — evita depender de libgdiplus dentro del contenedor Linux del
    /// Dockerfile) y embebido como data URI: nada de la clave viaja a
    /// ningún servicio externo para dibujar el código.
    /// </summary>
    public static string GenerarQrCodeDataUri(string uriOtpAuth)
    {
        using var generador = new QRCodeGenerator();
        using var datosQr = generador.CreateQrCode(uriOtpAuth, QRCodeGenerator.ECCLevel.M);
        var qrCode = new PngByteQRCode(datosQr);
        var bytesPng = qrCode.GetGraphic(10);

        return $"data:image/png;base64,{Convert.ToBase64String(bytesPng)}";
    }
}
