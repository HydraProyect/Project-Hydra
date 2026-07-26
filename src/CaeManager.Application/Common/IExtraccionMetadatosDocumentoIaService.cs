using CaeManager.Domain.Common;

namespace CaeManager.Application.Common;

/// <summary>
/// Lectura por IA de metadatos de un Documento (fecha de emisión, fecha de
/// vencimiento si aparece explícita, presencia de firma, tipo aparente) con
/// un nivel de confianza general — ver VerificacionIaDocumentoService, que
/// la usa para comparar lo detectado contra lo que introdujo el usuario y
/// decidir si hace falta revisión humana. Mismo patrón "inerte por
/// defecto" que IExtraccionTrabajadoresIaService: sin ApiKey configurada,
/// no hay verificación automática y la subida del documento sigue
/// funcionando igual.
/// </summary>
public interface IExtraccionMetadatosDocumentoIaService
{
    Task<Result<MetadatosDocumentoExtraidosDto>> ExtraerAsync(
        byte[] contenidoPdf, string nombreTipoDocumento, CancellationToken cancellationToken = default);
}

/// <summary>
/// <paramref name="ConfianzaGeneral"/> es 0-100. Las fechas y la firma
/// pueden venir a null cuando la IA no puede determinarlas con certeza
/// razonable — nunca se inventan (ver prompt maestro del Issue #19).
/// </summary>
public record MetadatosDocumentoExtraidosDto(
    string? TipoDetectado,
    DateOnly? FechaEmisionDetectada,
    DateOnly? FechaVencimientoDetectada,
    bool? TieneFirma,
    int ConfianzaGeneral,
    string? NotasValidacion);
