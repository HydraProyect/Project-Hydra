using CaeManager.Domain.Common;

namespace CaeManager.Domain.Documentos;

/// <summary>
/// Constancia de una firma en campo (trazo manuscrito capturado en pantalla)
/// estampada sobre el archivo de un Documento — evento histórico, 0..N por
/// Documento (una fila nueva por cada firma, sin <c>Indice</c>: a diferencia
/// de <see cref="FirmaDigitalDocumento"/> no se borran ni se recrean al
/// renovar, el orden natural es <see cref="FirmadoEnUtc"/>).
///
/// El trazo en sí NO se persiste aquí como blob — vive solo dentro de los
/// píxeles del PDF resultante (RGPD-TRATAMIENTO-DATOS.md § 5: un único
/// artefacto que purgar). Esta fila guarda únicamente metadatos + el hash
/// SHA-256 del PDF ya firmado, como constancia de integridad propia (sin
/// certificado — eso es la Fase B).
/// </summary>
public class FirmaEnCampoDocumento : EntidadConTenant
{
    public const int LongitudMaximaFirmanteNombre = 300;
    public const int LongitudMaximaFirmanteRol = 50;
    public const int LongitudMaximaUbicacion = 300;
    public const int LongitudHashSha256 = 64;

    public Guid DocumentoId { get; private set; }
    public Guid FirmanteUsuarioId { get; private set; }
    public string FirmanteNombre { get; private set; } = string.Empty;
    public string FirmanteRol { get; private set; } = string.Empty;
    public DateTime FirmadoEnUtc { get; private set; }
    public string? Ubicacion { get; private set; }
    public string HashSha256Pdf { get; private set; } = string.Empty;

    private FirmaEnCampoDocumento()
    {
    }

    public FirmaEnCampoDocumento(
        Guid documentoId,
        Guid firmanteUsuarioId,
        string firmanteNombre,
        string firmanteRol,
        DateTime firmadoEnUtc,
        string? ubicacion,
        string hashSha256Pdf)
    {
        if (documentoId == Guid.Empty)
            throw new ArgumentException("La firma en campo debe estar ligada a un documento.", nameof(documentoId));
        if (firmanteUsuarioId == Guid.Empty)
            throw new ArgumentException("La firma en campo debe tener un firmante.", nameof(firmanteUsuarioId));
        if (hashSha256Pdf?.Length != LongitudHashSha256)
            throw new ArgumentException(
                $"El hash SHA-256 debe tener exactamente {LongitudHashSha256} caracteres.", nameof(hashSha256Pdf));

        DocumentoId = documentoId;
        FirmanteUsuarioId = firmanteUsuarioId;
        FirmanteNombre = Truncar(firmanteNombre, LongitudMaximaFirmanteNombre) ?? string.Empty;
        FirmanteRol = Truncar(firmanteRol, LongitudMaximaFirmanteRol) ?? string.Empty;
        FirmadoEnUtc = firmadoEnUtc;
        Ubicacion = Truncar(ubicacion, LongitudMaximaUbicacion);
        HashSha256Pdf = hashSha256Pdf;
    }

    private static string? Truncar(string? valor, int longitudMaxima) =>
        valor?.Length > longitudMaxima ? valor[..longitudMaxima] : valor;
}
