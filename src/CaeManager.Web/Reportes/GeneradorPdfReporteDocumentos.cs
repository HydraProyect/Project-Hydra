using CaeManager.Application.Reportes.Queries;
using CaeManager.Web.Features.Documentos;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace CaeManager.Web.Reportes;

/// <summary>
/// Genera el PDF del reporte de vigencia documental dibujando la tabla a
/// mano con XGraphics — el volumen de filas esperado (decenas/cientos de
/// documentos, no miles) no justifica traer una librería de layout de
/// tablas completa. Ver ARCHITECTURE.md sobre por qué no se usa QuestPDF
/// (licencia por ingresos de la empresa, no del software).
/// </summary>
public static class GeneradorPdfReporteDocumentos
{
    private const double MargenIzquierdo = 40;
    private const double MargenSuperior = 40;
    private const double MargenInferior = 40;
    private const double AltoFila = 20;

    // Suman 515pt: ancho útil de una página A4 (595pt) menos los márgenes
    // izquierdo/derecho de 40pt cada uno — si no encajan exactamente, la
    // última columna se sale del área imprimible.
    private static readonly double[] AnchoColumnas = [55, 130, 120, 130, 80];
    private static readonly string[] Cabeceras = ["Estado", "Trabajador", "Empresa", "Tipo de documento", "Vencimiento"];

    public static byte[] Generar(IReadOnlyList<FilaReporteDocumentoDto> filas, DateTime generadoEnUtc)
    {
        using var documento = new PdfDocument();
        var fuenteTitulo = new XFont(EmbeddedFontResolver.NombreFuente, 16, XFontStyleEx.Bold);
        var fuenteSubtitulo = new XFont(EmbeddedFontResolver.NombreFuente, 9, XFontStyleEx.Regular);
        var fuenteCabecera = new XFont(EmbeddedFontResolver.NombreFuente, 9, XFontStyleEx.Bold);
        var fuenteCelda = new XFont(EmbeddedFontResolver.NombreFuente, 9, XFontStyleEx.Regular);

        PdfPage? pagina = null;
        XGraphics? graficos = null;
        double y = 0;
        double anchoUtil = 0;

        void NuevaPagina(bool conTitulo)
        {
            pagina = documento.AddPage();
            graficos = XGraphics.FromPdfPage(pagina);
            anchoUtil = pagina.Width.Point - 2 * MargenIzquierdo;
            y = MargenSuperior;

            if (conTitulo)
            {
                graficos.DrawString("Reporte de vigencia documental", fuenteTitulo, XBrushes.Black, new XPoint(MargenIzquierdo, y));
                y += 22;
                graficos.DrawString(
                    $"CAE Manager — generado el {generadoEnUtc:dd/MM/yyyy HH:mm} UTC — {filas.Count} documento(s)",
                    fuenteSubtitulo, XBrushes.Gray, new XPoint(MargenIzquierdo, y));
                y += 20;
            }

            DibujarCabeceraTabla();
        }

        void DibujarCabeceraTabla()
        {
            var x = MargenIzquierdo;
            for (var i = 0; i < Cabeceras.Length; i++)
            {
                graficos!.DrawString(Cabeceras[i], fuenteCabecera, XBrushes.Black, new XPoint(x, y));
                x += AnchoColumnas[i];
            }
            y += 4;
            graficos!.DrawLine(XPens.Gray, MargenIzquierdo, y, MargenIzquierdo + anchoUtil, y);
            y += AltoFila - 8;
        }

        NuevaPagina(conTitulo: true);

        foreach (var fila in filas)
        {
            if (y + AltoFila > pagina!.Height.Point - MargenInferior)
                NuevaPagina(conTitulo: false);

            var x = MargenIzquierdo;
            var valores = new[]
            {
                EstadoDocumentoUi.Texto(fila.Estado),
                fila.TrabajadorNombre,
                fila.EmpresaRazonSocial,
                fila.TipoDocumentoNombre,
                fila.FechaVencimiento?.ToString("dd/MM/yyyy") ?? "—"
            };

            for (var i = 0; i < valores.Length; i++)
            {
                graficos!.DrawString(valores[i], fuenteCelda, XBrushes.Black, new XPoint(x, y));
                x += AnchoColumnas[i];
            }
            y += AltoFila;
        }

        using var stream = new MemoryStream();
        documento.Save(stream);
        return stream.ToArray();
    }
}
