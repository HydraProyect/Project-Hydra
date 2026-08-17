using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace CaeManager.Web.Reportes;

/// <summary>
/// Generador de PDF genérico para cualquier informe tabular de Reportes
/// (antes GeneradorPdfReporteDocumentos, específico de vigencia documental
/// — generalizado para servir también "Asignaciones activas" sin duplicar
/// el dibujado a mano con XGraphics). Mismo criterio que antes: el volumen
/// esperado (decenas/cientos de filas) no justifica una librería de layout
/// de tablas completa — ver ARCHITECTURE.md sobre por qué no se usa
/// QuestPDF (licencia por ingresos de la empresa, no del software).
/// </summary>
public static class GeneradorPdfInforme
{
    private const double MargenIzquierdo = 40;
    private const double MargenSuperior = 40;
    private const double MargenInferior = 40;
    private const double AltoFila = 20;

    public static byte[] Generar(
        string titulo, string subtitulo, string[] cabeceras, double[] anchoColumnas, IReadOnlyList<string[]> filas)
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
                graficos.DrawString(titulo, fuenteTitulo, XBrushes.Black, new XPoint(MargenIzquierdo, y));
                y += 22;
                graficos.DrawString(subtitulo, fuenteSubtitulo, XBrushes.Gray, new XPoint(MargenIzquierdo, y));
                y += 20;
            }

            DibujarCabeceraTabla();
        }

        void DibujarCabeceraTabla()
        {
            var x = MargenIzquierdo;
            for (var i = 0; i < cabeceras.Length; i++)
            {
                graficos!.DrawString(cabeceras[i], fuenteCabecera, XBrushes.Black, new XPoint(x, y));
                x += anchoColumnas[i];
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
            for (var i = 0; i < fila.Length; i++)
            {
                graficos!.DrawString(fila[i], fuenteCelda, XBrushes.Black, new XPoint(x, y));
                x += anchoColumnas[i];
            }
            y += AltoFila;
        }

        using var stream = new MemoryStream();
        documento.Save(stream);
        return stream.ToArray();
    }
}
