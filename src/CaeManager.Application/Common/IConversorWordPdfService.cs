namespace CaeManager.Application.Common;

/// <summary>
/// Convierte un documento Word (.docx) a PDF. La implementación real vive en
/// Infrastructure sobre LibreOffice headless (ver ARCHITECTURE.md,
/// "Archivos") — Application solo conoce el contrato.
/// </summary>
public interface IConversorWordPdfService
{
    Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default);
}
