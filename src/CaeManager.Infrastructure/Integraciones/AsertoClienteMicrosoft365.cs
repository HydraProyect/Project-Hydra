using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using CaeManager.Domain.Common;

namespace CaeManager.Infrastructure.Integraciones;

/// <summary>
/// Construye el <c>client_assertion</c> con el que un App Registration de Entra ID
/// se autentica ante el endpoint de tokens usando un <b>certificado</b> en vez de
/// un secreto de cliente (RFC 7523 / documentación «Microsoft identity platform
/// application authentication certificate credentials»): un JWT firmado con la
/// clave privada RSA del certificado, cuya parte pública se sube al App Registration.
///
/// No usa ninguna librería de tokens a propósito: son ~30 líneas, evita añadir una
/// dependencia (y su licencia) a Infrastructure, y así queda entero a la vista lo
/// que se firma. Lo que NUNCA hace: escribir la clave privada, loguear rutas junto
/// a contenido, ni devolver material de la clave en un error.
/// </summary>
public static class AsertoClienteMicrosoft365
{
    public const string TipoAserto = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    /// <summary>Vigencia del aserto: Entra rechaza los que caducan a más de ~10 min.</summary>
    public static readonly TimeSpan Vigencia = TimeSpan.FromMinutes(10);

    public static Result<string> Crear(
        string clientId, string audiencia, string certificadoRuta, string clavePrivadaRuta, DateTimeOffset ahora)
    {
        try
        {
            using var certificado = X509Certificate2.CreateFromPemFile(certificadoRuta, clavePrivadaRuta);
            using var rsa = certificado.GetRSAPrivateKey();
            if (rsa is null)
            {
                return Result.Fallo<string>(Error.Crear(
                    "Integraciones.Microsoft365.CertificadoInvalido",
                    "El certificado configurado no tiene una clave privada RSA utilizable."));
            }

            // x5t (SHA-1) y x5t#S256 (SHA-256) identifican QUÉ certificado del App
            // Registration valida la firma. Se envían los dos: cada versión del
            // endpoint de tokens acepta uno u otro.
            var cabecera = new Dictionary<string, string>
            {
                ["alg"] = "RS256",
                ["typ"] = "JWT",
                ["x5t"] = Base64Url(certificado.GetCertHash()),
                ["x5t#S256"] = Base64Url(SHA256.HashData(certificado.RawData)),
            };

            var inicio = ahora.ToUnixTimeSeconds();
            var reclamaciones = new Dictionary<string, object>
            {
                ["aud"] = audiencia,
                ["iss"] = clientId,
                ["sub"] = clientId,
                ["jti"] = Guid.NewGuid().ToString(),
                ["nbf"] = inicio,
                ["iat"] = inicio,
                ["exp"] = ahora.Add(Vigencia).ToUnixTimeSeconds(),
            };

            var contenido = $"{Base64Url(JsonSerializer.SerializeToUtf8Bytes(cabecera))}.{Base64Url(JsonSerializer.SerializeToUtf8Bytes(reclamaciones))}";
            var firma = rsa.SignData(Encoding.ASCII.GetBytes(contenido), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            return Result.Exito($"{contenido}.{Base64Url(firma)}");
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or ArgumentException or UnauthorizedAccessException)
        {
            // El mensaje de la excepción puede nombrar la ruta; nunca el contenido de la clave.
            return Result.Fallo<string>(Error.Crear(
                "Integraciones.Microsoft365.CertificadoNoLegible",
                "No se pudo leer el certificado configurado o su clave privada."));
        }
    }

    private static string Base64Url(byte[] datos) =>
        Convert.ToBase64String(datos).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
