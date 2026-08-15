namespace CaeManager.Application.Common;

/// <summary>
/// Lee los campos de un AcroForm directamente de la estructura del PDF —
/// nombre, página y posición ya vienen en el propio archivo, no hace falta
/// IA (ADR-010 § 4, "AcroForm field names"). Solo aplica a
/// <c>FormatoOrigenPlantilla.PdfConCampos</c>; para <c>PdfVisual</c> la
/// detección pasa por <c>IDocumentAIRouterService</c> en su lugar.
/// </summary>
public interface IExtractorCamposAcroFormService
{
    IReadOnlyList<CampoAcroFormDetectado> Extraer(byte[] pdfOriginal);
}

/// <summary>
/// Posición en el mismo sistema de coordenadas que usa
/// <c>IRellenadorPlantillaPdfService</c> (origen arriba-izquierda, como
/// <c>XGraphics</c>) — no el sistema nativo de PDF (origen abajo-izquierda,
/// el que devuelve <c>/Rect</c>). La conversión ya está hecha al llegar aquí.
/// </summary>
public record CampoAcroFormDetectado(
    string NombreCampo,
    int Pagina,
    double X,
    double Y,
    double Ancho,
    double Alto);
