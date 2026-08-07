using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Common;

/// <summary>
/// Verifica criptográficamente las firmas digitales de un PDF (PAdES /
/// PKCS#7 detached): integridad del rango firmado, cadena de confianza
/// contra un almacén propio y revocación con degradación explícita — ver
/// PLAN-FIRMA-DIGITAL-PDF.md § 3 (motor .NET nativo). No decide nada sobre
/// el Documento: devuelve hechos; la decisión de auto-validar la toma
/// ValidacionDocumentoOficialService con sus propias reglas.
///
/// Falla (Result) solo si el archivo no es un PDF procesable. Un PDF sin
/// firmas es un resultado exitoso con <see cref="NivelConfianzaDocumental.SoloLectura"/>
/// — la mayoría de los documentos del CAE son escaneos sin firma y eso no
/// es un error.
/// </summary>
public interface IVerificadorFirmaPdfService
{
    /// <summary>
    /// byte[] y no Stream a propósito, por coherencia con
    /// <see cref="IExtractorTextoDigitalService"/> y porque los documentos
    /// oficiales en alcance pesan menos de 1 MB; si la épica 2 trae
    /// documentación grande, se revisará la firma completa.
    /// </summary>
    Task<Result<ResultadoVerificacionFirmasPdf>> VerificarAsync(byte[] contenidoPdf, CancellationToken cancellationToken = default);
}

/// <summary>
/// <paramref name="Nivel"/> agrega todas las firmas del archivo: cualquier
/// firma inválida lo hace <see cref="NivelConfianzaDocumental.Manipulado"/>;
/// sin firmas es <see cref="NivelConfianzaDocumental.SoloLectura"/>; y la
/// confianza/revocación se evalúan sobre la firma válida que cubre el final
/// del archivo (contenido añadido después de la última firma impide superar
/// <see cref="NivelConfianzaDocumental.FirmaIntegraEmisorNoConfiable"/>).
/// </summary>
/// <summary>
/// <paramref name="AparentaReimpresion"/>: sin firmas y con señales de
/// imprimir-a-PDF (Producer de impresión o cero fuentes embebidas = página
/// rasterizada). Calibrado con muestras reales de gestorías: reimprimir
/// destruye la firma y la capa de texto — el motivo que ve el usuario debe
/// pedir el original de la Sede, no decir solo "sin firma".
/// </summary>
public record ResultadoVerificacionFirmasPdf(
    NivelConfianzaDocumental Nivel,
    IReadOnlyList<FirmaPdfVerificada> Firmas,
    bool AparentaReimpresion = false);

/// <summary>
/// Hechos verificados de una firma concreta. <paramref name="FirmanteNombre"/> y
/// <paramref name="FirmanteNif"/> vienen del certificado y pueden pertenecer a una
/// persona física — quién los persiste debe respetar la restricción RGPD del
/// plan (§ PR-4: solo sellos de órgano hasta confirmación del usuario).
/// </summary>
public record FirmaPdfVerificada(
    int Indice,
    EstadoFirmaPdf Estado,
    bool CadenaConfiable,
    ComprobacionRevocacion Revocacion,
    string EmisorCertificado,
    string NumeroSerieCertificado,
    bool EsSelloDeOrgano,
    string? FirmanteNombre,
    string? FirmanteNif,
    DateTime? FechaFirmaUtc,
    bool TieneSelloDeTiempo,
    bool CubreDocumentoCompleto,
    string? MotivoInvalidez);
