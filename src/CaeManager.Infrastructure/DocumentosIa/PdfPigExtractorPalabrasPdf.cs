using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores.Extraccion;
using CaeManager.Domain.Common;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace CaeManager.Infrastructure.DocumentosIa;

/// <summary>
/// Lee las palabras de un PDF con su caja en la página, con PdfPig (Apache-2.0), para el parser
/// local de Trabajadores (<see cref="ExtractorTrabajadoresLocalService"/>). PDFsharp, que usa el
/// resto del proyecto, no expone texto con posición.
///
/// El PDF lo ha preparado un tercero (lo subió un Tenant o llegó adjunto), así que antes de
/// abrirlo se aplica el mismo tope de páginas que a los demás lectores
/// (<see cref="LectorRecuentoPaginasPdfSinAbrir"/>), se abre sin exigir las fuentes y cualquier fallo
/// —cifrado, estructura rota, fuente ilegible— es un <c>Result</c> fallido: el compositor lo trata
/// como «no cuadra» y lee con IA. Los mensajes de excepción no se registran, porque pueden
/// reproducir contenido del documento.
/// </summary>
public sealed class PdfPigExtractorPalabrasPdf(ILogger<PdfPigExtractorPalabrasPdf> logger) : IExtractorPalabrasPdf
{
    private const int MaximoPaginasDocumento = 2000;

    public Result<IReadOnlyList<PaginaPdf>> ExtraerPalabras(byte[] contenidoPdf)
    {
        if (LectorRecuentoPaginasPdfSinAbrir.IntentarLeerRecuentoDePaginasSinAbrir(contenidoPdf) is { } declaradas
            && declaradas > MaximoPaginasDocumento)
        {
            logger.LogWarning(
                "PDF rechazado antes de abrir: declara {PaginasDeclaradas} páginas, por encima del máximo de {MaximoPaginas}.",
                declaradas, MaximoPaginasDocumento);

            return Fallo("ExtractorPalabrasPdf.DemasiadasPaginas");
        }

        try
        {
            using var documento = PdfDocument.Open(contenidoPdf, new ParsingOptions { SkipMissingFonts = true });

            if (documento.NumberOfPages > MaximoPaginasDocumento)
                return Fallo("ExtractorPalabrasPdf.DemasiadasPaginas");

            var paginas = new List<PaginaPdf>(documento.NumberOfPages);

            foreach (var pagina in documento.GetPages())
            {
                // Texto en diagonal o vertical (marcas de agua, sellos) no forma parte de ninguna tabla.
                var palabras = pagina.GetWords(NearestNeighbourWordExtractor.Instance)
                    .Where(w => w.TextOrientation == TextOrientation.Horizontal)
                    .Select(w => new PalabraPdf(w.Text, w.BoundingBox.Left, w.BoundingBox.Right, w.BoundingBox.Bottom, w.BoundingBox.Top))
                    .ToList();

                paginas.Add(new PaginaPdf(pagina.Number, palabras, ProporcionDeImagenMayor(pagina)));
            }

            return Result.Exito<IReadOnlyList<PaginaPdf>>(paginas);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("PdfPig no pudo leer el PDF ({TipoExcepcion}, {Bytes} bytes).", ex.GetType().Name, contenidoPdf.Length);
            return Fallo("ExtractorPalabrasPdf.ArchivoInvalido");
        }
    }

    private static double ProporcionDeImagenMayor(Page pagina)
    {
        var areaDePagina = pagina.Width * pagina.Height;
        if (areaDePagina <= 0)
            return 0;

        var mayor = 0d;
        foreach (var imagen in pagina.GetImages())
            mayor = Math.Max(mayor, Math.Abs(imagen.BoundingBox.Width * imagen.BoundingBox.Height));

        return Math.Min(1d, mayor / areaDePagina);
    }

    private static Result<IReadOnlyList<PaginaPdf>> Fallo(string codigo) =>
        Result.Fallo<IReadOnlyList<PaginaPdf>>(Error.Crear(codigo, "No pudimos leer el texto de este archivo."));
}
