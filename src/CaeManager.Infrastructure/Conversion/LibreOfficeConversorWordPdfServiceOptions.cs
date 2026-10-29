namespace CaeManager.Infrastructure.Conversion;

public class LibreOfficeConversorWordPdfServiceOptions
{
    public const string SeccionConfiguracion = "ConversionWordPdf";

    /// <summary>Ejecutable de LibreOffice headless. El Dockerfile de despliegue instala "libreoffice-writer", que lo deja en el PATH como "soffice".</summary>
    public string RutaEjecutable { get; set; } = "soffice";

    public int TimeoutSegundos { get; set; } = 60;

    /// <summary>
    /// Raíz de los directorios temporales de conversión (uno por conversión, con nombre
    /// aleatorio). Por defecto cuelga de la carpeta de la aplicación y no del temporal
    /// global compartido. En la imagen la crea el Dockerfile con el usuario no root como
    /// propietario; no es <c>App_Data</c> a propósito, porque crearla allí activaría el
    /// sumidero de fichero de los logs.
    /// </summary>
    public string RutaTemporales { get; set; } = Path.Combine(AppContext.BaseDirectory, "temporales-conversion-word");
}
