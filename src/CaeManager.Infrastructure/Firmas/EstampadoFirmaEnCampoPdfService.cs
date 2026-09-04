using System.Security.Cryptography;
using CaeManager.Application.Common;
using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;

namespace CaeManager.Infrastructure.Firmas;

/// <summary>
/// Añade una página nueva al final del PDF con el trazo de la firma y los
/// metadatos de quién/cuándo firmó — nunca dibuja sobre el contenido
/// existente, para no arriesgar solapes impredecibles con el documento
/// original. Sin certificado propio (Fase B, ver
/// <see cref="IEstampadoFirmaEnCampoPdfService"/>).
///
/// "DejaVu Sans" resuelve porque <c>GlobalFontSettings.FontResolver</c> se
/// fija una vez, al arrancar, en CaeManager.Web/Program.cs
/// (<c>EmbeddedFontResolver</c>) — dependencia implícita entre esta clase de
/// Infrastructure y ese registro global de Web; sin él, PDFsharp 6.x (que no
/// usa fuentes del sistema operativo) no podría resolver este nombre.
/// </summary>
public class EstampadoFirmaEnCampoPdfService : IEstampadoFirmaEnCampoPdfService
{
    private const string NombreFuente = "DejaVu Sans";
    private const double AnchoCajaImagen = 200;
    private const double AltoCajaImagen = 80;
    private const double SeparacionCajas = 20;
    private const double Margen = 40;

    /// <summary>
    /// REC-186: pdfOriginal es <c>documento.ArchivoUrl</c> —un Documento que
    /// un tenant subió (CrearDocumentoCommand) o renovó
    /// (RenovarDocumentoCommand), o generó desde una plantilla— y este es
    /// uno de los dos sitios que abren en
    /// <see cref="PdfDocumentOpenMode.Modify"/> (711-754 ms sobre 20 000
    /// páginas, el modo más caro de los cuatro, medido). Mismo umbral
    /// reutilizado que el resto de sitios de este incremento.
    /// </summary>
    private const int MaximoPaginasDocumento = 2000;

    public byte[] Estampar(
        byte[] pdfOriginal,
        byte[] firmaPng,
        byte[]? selloPng,
        string firmanteNombre,
        string firmanteRol,
        DateTime firmadoEnUtc,
        string? ubicacion)
    {
        // ANTES de abrir con PdfReader — ver el doc-comment de
        // MaximoPaginasDocumento. Abstención (null) no cambia nada.
        if (LectorRecuentoPaginasPdfSinAbrir.IntentarLeerRecuentoDePaginasSinAbrir(pdfOriginal) is { } paginasDeclaradas &&
            paginasDeclaradas > MaximoPaginasDocumento)
        {
            throw new InvalidDataException(
                $"El documento original declara más de {MaximoPaginasDocumento} páginas y no se puede procesar.");
        }

        using var flujoOrigen = new MemoryStream(pdfOriginal);
        using var documento = PdfReader.Open(flujoOrigen, PdfDocumentOpenMode.Modify);

        var pagina = documento.AddPage();
        using var graficos = XGraphics.FromPdfPage(pagina);

        var fuenteTitulo = new XFont(NombreFuente, 14, XFontStyleEx.Bold);
        var fuenteTexto = new XFont(NombreFuente, 10, XFontStyleEx.Regular);

        var y = Margen;
        graficos.DrawString("Constancia de firma en campo", fuenteTitulo, XBrushes.Black, new XPoint(Margen, y));
        y += 30;

        DibujarImagenEnCaja(graficos, firmaPng, Margen, y, AnchoCajaImagen, AltoCajaImagen);
        if (selloPng is not null)
        {
            // Firma y sello lado a lado, como en un documento físico.
            DibujarImagenEnCaja(graficos, selloPng, Margen + AnchoCajaImagen + SeparacionCajas, y, AnchoCajaImagen, AltoCajaImagen);
        }
        y += AltoCajaImagen + 20;

        graficos.DrawLine(XPens.Gray, Margen, y, pagina.Width.Point - Margen, y);
        y += 20;

        var fechaLocal = firmadoEnUtc.ToLocalTime();
        graficos.DrawString(
            $"Firmado por {firmanteNombre} ({firmanteRol}) el {fechaLocal:dd/MM/yyyy HH:mm}",
            fuenteTexto, XBrushes.Black, new XPoint(Margen, y));
        y += 18;

        if (!string.IsNullOrWhiteSpace(ubicacion))
        {
            graficos.DrawString($"Ubicación: {ubicacion}", fuenteTexto, XBrushes.Black, new XPoint(Margen, y));
            y += 18;
        }

        // Hash del archivo ORIGINAL (previo al estampado, calculable aquí sin
        // circularidad) — ancla la constancia al contenido exacto que se
        // firmó. El hash del PDF ya estampado (que sí identifica el archivo
        // final) lo calcula y persiste el Command handler después de esta
        // llamada, en FirmaEnCampoDocumento.HashSha256Pdf.
        var hashOriginal = Convert.ToHexStringLower(SHA256.HashData(pdfOriginal));
        graficos.DrawString(
            $"Referencia del archivo original: {hashOriginal[..16]}…",
            fuenteTexto, XBrushes.Gray, new XPoint(Margen, y));

        using var salida = new MemoryStream();
        documento.Save(salida);
        return salida.ToArray();
    }

    /// <summary>Escalado proporcional dentro de una caja fija, sin deformar ni recortar la imagen.</summary>
    private static void DibujarImagenEnCaja(
        XGraphics graficos, byte[] imagenPng, double x, double y, double anchoCaja, double altoCaja)
    {
        using var flujoImagen = new MemoryStream(imagenPng);
        using var xImagen = XImage.FromStream(flujoImagen);

        var anchoImagen = xImagen.PixelWidth * 72.0 / xImagen.HorizontalResolution;
        var altoImagen = xImagen.PixelHeight * 72.0 / xImagen.VerticalResolution;
        var factor = Math.Min(anchoCaja / anchoImagen, altoCaja / altoImagen);

        graficos.DrawImage(xImagen, x, y, anchoImagen * factor, altoImagen * factor);
    }
}
