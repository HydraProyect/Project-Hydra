namespace CaeManager.Infrastructure.Conversion;

public class LibreOfficeConversorWordPdfServiceOptions
{
    public const string SeccionConfiguracion = "ConversionWordPdf";

    /// <summary>Ejecutable de LibreOffice headless. El Dockerfile de despliegue instala "libreoffice-writer", que lo deja en el PATH como "soffice".</summary>
    public string RutaEjecutable { get; set; } = "soffice";

    public int TimeoutSegundos { get; set; } = 60;
}
