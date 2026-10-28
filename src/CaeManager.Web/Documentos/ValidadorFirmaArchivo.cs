namespace CaeManager.Web.Documentos;

/// <summary>
/// Valida el contenido real de un archivo subido por sus primeros bytes
/// (firma / "magic number"), no por su extensión — el filtro de
/// <see cref="ConversorArchivosPdf"/> (EsPdf/EsImagen/EsWord) solo mira el
/// nombre, así que un archivo renombrado a mano (p. ej. un ejecutable
/// guardado como ".pdf") lo pasaría sin más comprobación. La extensión
/// decide qué firma se espera; este validador comprueba que el contenido
/// la cumple de verdad antes de convertirlo (PdfSharp/LibreOffice) o
/// guardarlo — P1-16 de Project-Hydra-Negocio/MATURITY_REVIEW.md.
/// </summary>
public static class ValidadorFirmaArchivo
{
    public static bool TieneFirmaValida(byte[] contenido, string nombreArchivo) =>
        Application.Common.Archivos.ValidadorFirmaArchivo.TieneFirmaValida(contenido, nombreArchivo);

    public static bool EsDocumentoValido(byte[] contenido, string nombreArchivo) =>
        Application.Common.Archivos.ValidadorFirmaArchivo.EsDocumentoValido(contenido, nombreArchivo);
}
