namespace CaeManager.Application.Common;

/// <summary>
/// Convierte un documento Word (.docx) a PDF. La implementación real vive en
/// Infrastructure sobre LibreOffice headless (ver Project-Hydra-Negocio/tecnico/ARCHITECTURE.md,
/// "Archivos") — Application solo conoce el contrato.
/// </summary>
public interface IConversorWordPdfService
{
    Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default);
}

/// <summary>
/// La conversión de Word a PDF no empezó porque otra conversión seguía ocupando el
/// servicio durante toda la espera máxima. Es transitoria: quien sube el documento ve
/// <see cref="MensajeUsuario"/> en vez de un fallo genérico, y puede reintentar o subir
/// el PDF ya convertido. Deriva de <see cref="InvalidOperationException"/> para que
/// quien ya trataba cualquier fallo de conversión siga tratándolo.
/// </summary>
public sealed class ConversionWordPdfOcupadaException() : InvalidOperationException(MensajeUsuario)
{
    /// <summary>Lo que ve el usuario: no lleva datos del documento ni del Tenant.</summary>
    public const string MensajeUsuario =
        "La conversión de documentos Word está ocupada con otros archivos. Inténtalo de nuevo en un minuto o sube el documento ya convertido a PDF.";
}
