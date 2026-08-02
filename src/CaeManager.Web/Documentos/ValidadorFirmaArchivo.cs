namespace CaeManager.Web.Documentos;

/// <summary>
/// Valida el contenido real de un archivo subido por sus primeros bytes
/// (firma / "magic number"), no por su extensión — el filtro de
/// <see cref="ConversorArchivosPdf"/> (EsPdf/EsImagen/EsWord) solo mira el
/// nombre, así que un archivo renombrado a mano (p. ej. un ejecutable
/// guardado como ".pdf") lo pasaría sin más comprobación. La extensión
/// decide qué firma se espera; este validador comprueba que el contenido
/// la cumple de verdad antes de convertirlo (PdfSharp/LibreOffice) o
/// guardarlo — P1-16 de docs/business/MATURITY_REVIEW.md.
/// </summary>
public static class ValidadorFirmaArchivo
{
    private static readonly byte[] FirmaPdf = "%PDF-"u8.ToArray();
    private static readonly byte[] FirmaJpeg = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] FirmaPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // .docx es un ZIP (Office Open XML) — cabecera local de fichero ZIP.
    private static readonly byte[] FirmaZip = [0x50, 0x4B, 0x03, 0x04];

    /// <summary>
    /// Comprueba que <paramref name="contenido"/> empieza con la firma del
    /// tipo que declara la extensión de <paramref name="nombreArchivo"/>.
    /// Una extensión que <see cref="ConversorArchivosPdf"/> no reconoce no es
    /// responsabilidad de este método — eso ya se valida antes de llegar aquí.
    /// </summary>
    public static bool TieneFirmaValida(byte[] contenido, string nombreArchivo)
    {
        if (ConversorArchivosPdf.EsPdf(nombreArchivo))
            return EmpiezaCon(contenido, FirmaPdf);

        if (ConversorArchivosPdf.EsImagen(nombreArchivo))
            return EmpiezaCon(contenido, FirmaJpeg) || EmpiezaCon(contenido, FirmaPng);

        if (ConversorArchivosPdf.EsWord(nombreArchivo))
            return EmpiezaCon(contenido, FirmaZip);

        return false;
    }

    private static bool EmpiezaCon(byte[] contenido, byte[] firma) =>
        contenido.Length >= firma.Length && contenido.AsSpan(0, firma.Length).SequenceEqual(firma);
}
