using CaeManager.Domain.Common;

namespace CaeManager.Domain.DocumentosIa;

/// <summary>
/// Cache documental por hash (ver docs/ARQUITECTURA-IA-DOCUMENTAL.md § 3):
/// antes de llamar a ningún proveedor, el router comprueba si ya se
/// procesó exactamente este mismo archivo — si es así, reutiliza el
/// resultado guardado en vez de volver a pagar por la misma extracción.
/// Clave deliberadamente simple (solo el hash, no hash+tipo esperado): el
/// caso real que evita es "el mismo archivo físico se sube dos veces", que
/// casi siempre ocurre en el mismo contexto — ver nota de alcance en
/// ROADMAP.md si esto deja de ser cierto.
/// </summary>
public class ExtraccionIaCache : EntidadConTenant
{
    public const int LongitudHash = 64; // SHA256 en hexadecimal

    public string HashSha256 { get; private set; } = string.Empty;
    public string ExtraccionJson { get; private set; } = string.Empty;
    public DateTime CreadaEnUtc { get; private set; } = DateTime.UtcNow;

    private ExtraccionIaCache()
    {
    }

    private ExtraccionIaCache(string hashSha256, string extraccionJson)
    {
        if (string.IsNullOrWhiteSpace(hashSha256) || hashSha256.Length != LongitudHash)
            throw new ArgumentException($"El hash SHA256 debe tener exactamente {LongitudHash} caracteres.", nameof(hashSha256));
        if (string.IsNullOrWhiteSpace(extraccionJson))
            throw new ArgumentException("El JSON de extracción no puede estar vacío.", nameof(extraccionJson));

        HashSha256 = hashSha256;
        ExtraccionJson = extraccionJson;
    }

    public static ExtraccionIaCache Crear(string hashSha256, string extraccionJson) => new(hashSha256, extraccionJson);
}
