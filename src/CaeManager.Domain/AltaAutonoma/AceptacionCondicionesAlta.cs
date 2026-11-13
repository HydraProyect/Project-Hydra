using CaeManager.Domain.Common;

namespace CaeManager.Domain.AltaAutonoma;

/// <summary>Lo que declara quien acepta un documento de condiciones durante el alta autónoma.</summary>
public sealed record AceptacionDeclarada(
    string Documento, string Version, string HashSha256, bool DeclaraFacultades, string Ip, DateTime AceptadaEnUtc);

/// <summary>
/// Aceptación de un documento de condiciones en una <see cref="SolicitudAlta"/>: qué documento, qué
/// versión, el SHA-256 del texto aceptado, si quien acepta declara tener facultades para obligar a la
/// organización, desde qué IP y cuándo. Es la evidencia previa al alta; cuando nace el Tenant, el
/// Command del alta la materializa en <see cref="Cumplimiento.AceptacionContractualTenant"/> con
/// <c>RegistroAceptacionContractualAlta</c> (#2009).
/// </summary>
public class AceptacionCondicionesAlta : Entity
{
    public const int LongitudMaximaDocumento = 100;
    public const int LongitudMaximaVersion = 50;
    public const int LongitudHash = 64;
    public const int LongitudMaximaIp = 45;

    public Guid SolicitudAltaId { get; private set; }
    public string Documento { get; private set; } = string.Empty;
    public string Version { get; private set; } = string.Empty;
    public string HashSha256 { get; private set; } = string.Empty;
    public bool DeclaraFacultades { get; private set; }
    public string Ip { get; private set; } = string.Empty;
    public DateTime AceptadaEnUtc { get; private set; }

    private AceptacionCondicionesAlta()
    {
        // Requerido por EF Core.
    }

    internal AceptacionCondicionesAlta(Guid solicitudAltaId, AceptacionDeclarada declarada)
    {
        ArgumentNullException.ThrowIfNull(declarada);

        SolicitudAltaId = solicitudAltaId;
        Documento = Texto(declarada.Documento, LongitudMaximaDocumento, nameof(declarada.Documento));
        Version = Texto(declarada.Version, LongitudMaximaVersion, nameof(declarada.Version));
        HashSha256 = HashHexadecimal(declarada.HashSha256);
        DeclaraFacultades = declarada.DeclaraFacultades;
        Ip = SolicitudAlta.NormalizarIp(declarada.Ip, nameof(declarada.Ip));
        AceptadaEnUtc = SolicitudAlta.ExigirUtc(declarada.AceptadaEnUtc, nameof(declarada.AceptadaEnUtc));
    }

    private static string Texto(string? valor, int maximo, string parametro)
    {
        var limpio = valor?.Trim();
        if (string.IsNullOrEmpty(limpio) || limpio.Length > maximo)
            throw new ArgumentException($"El campo {parametro} es obligatorio y no puede superar {maximo} caracteres.", parametro);
        return limpio;
    }

    private static string HashHexadecimal(string? hash)
    {
        var limpio = hash?.Trim().ToLowerInvariant();
        if (limpio is null || limpio.Length != LongitudHash || !limpio.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new ArgumentException($"El hash SHA-256 del documento aceptado debe tener {LongitudHash} caracteres hexadecimales.", nameof(hash));
        return limpio;
    }
}
