using System.Security.Cryptography;
using System.Text;

namespace CaeManager.Infrastructure.Autenticacion;

/// <summary>
/// Lectura única de la cabecera <c>Authorization: ApiKey {clave}</c> de la API pública. La
/// comparten <see cref="ApiKeyAuthenticationHandler"/> (que autentica) y el limitador de la API
/// (que reparte el cupo antes de autenticar): si cada uno interpretara la cabecera a su manera,
/// dos variantes del mismo valor que el handler resuelve a la misma clave caerían en cupos
/// distintos del limitador y bastaría con variar espacios o mayúsculas para multiplicar el cupo.
/// </summary>
public static class CabeceraClaveApi
{
    private const string EsquemaCabecera = "ApiKey";

    /// <summary>
    /// Devuelve la clave en claro de una cabecera <c>Authorization</c>, o <c>null</c> si la cabecera
    /// no es del esquema <c>ApiKey</c> (ausente, <c>Bearer</c>, etc.). Una cabecera del esquema con
    /// la clave vacía devuelve la cadena vacía: no es "otro esquema", es una clave que falta.
    /// </summary>
    public static string? ExtraerClave(string? cabeceraAuthorization)
    {
        if (cabeceraAuthorization is null
            || !cabeceraAuthorization.StartsWith($"{EsquemaCabecera} ", StringComparison.OrdinalIgnoreCase))
            return null;

        return cabeceraAuthorization[(EsquemaCabecera.Length + 1)..].Trim();
    }

    /// <summary>SHA-256 hexadecimal en minúsculas de la clave: lo que se guarda y se busca en base.</summary>
    public static string HashDeClave(string claveEnClaro) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(claveEnClaro))).ToLowerInvariant();

    /// <summary>
    /// Identificador de la clave para repartir cupos en memoria: SHA-256 de la clave con un prefijo de
    /// dominio, de modo que no coincide con el hash que se guarda en base ni deja la clave en claro en
    /// ninguna estructura que alguien pudiera volcar o registrar.
    /// </summary>
    public static string HuellaParaLimitador(string claveEnClaro) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("limitador-api-publica:" + claveEnClaro)))
            .ToLowerInvariant();
}
