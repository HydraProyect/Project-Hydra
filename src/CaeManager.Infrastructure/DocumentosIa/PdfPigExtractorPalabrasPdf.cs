using System.Diagnostics;
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
/// El PDF lo ha preparado un tercero (lo subió un Tenant o llegó adjunto), así que se acota antes y
/// durante la lectura: tamaño del archivo (por defecto el mismo tope que la subida,
/// <see cref="LimitesArchivoSubido.TamanoMaximoBytes"/>; Dividir pide el suyo), páginas declaradas
/// (<see cref="LectorRecuentoPaginasPdfSinAbrir"/>) y reales (se cuentan al recorrerlas, porque
/// el recuento declarado puede mentir), palabras en total y un presupuesto de tiempo que se
/// comprueba entre páginas. Lo que no se puede acotar desde fuera es el coste de decodificar
/// una sola página: PdfPig corre en el proceso y no admite cancelación a mitad de página; es el
/// mismo riesgo que ya asumen los lectores de PDFsharp del proyecto. Cualquier fallo —cifrado,
/// estructura rota, fuente ilegible, tope superado— es un <c>Result</c> fallido: el compositor lo
/// trata como «no cuadra» y lee con IA. Los mensajes de excepción no se registran, porque pueden
/// reproducir contenido del documento.
/// </summary>
public sealed class PdfPigExtractorPalabrasPdf(ILogger<PdfPigExtractorPalabrasPdf> logger) : IExtractorPalabrasPdf
{
    public Result<IReadOnlyList<PaginaPdf>> ExtraerPalabras(byte[] contenidoPdf) =>
        ExtraerPalabras(contenidoPdf, LimitesArchivoSubido.TamanoMaximoBytes);

    public Result<IReadOnlyList<PaginaPdf>> ExtraerPalabras(byte[] contenidoPdf, long tamanoMaximoBytes)
    {
        if (contenidoPdf.Length > tamanoMaximoBytes)
            return Fallo("ExtractorPalabrasPdf.ArchivoDemasiadoGrande");

        if (LectorRecuentoPaginasPdfSinAbrir.IntentarLeerRecuentoDePaginasSinAbrir(contenidoPdf) is { } declaradas
            && declaradas > LimitesLecturaPalabrasPdf.MaximoPaginas)
        {
            logger.LogWarning(
                "PDF rechazado antes de abrir: declara {PaginasDeclaradas} páginas, por encima del máximo de {MaximoPaginas}.",
                declaradas, LimitesLecturaPalabrasPdf.MaximoPaginas);

            return Fallo("ExtractorPalabrasPdf.DemasiadasPaginas");
        }

        var reloj = Stopwatch.StartNew();

        try
        {
            using var documento = PdfDocument.Open(contenidoPdf, new ParsingOptions { SkipMissingFonts = true });

            if (documento.NumberOfPages > LimitesLecturaPalabrasPdf.MaximoPaginas)
                return Fallo("ExtractorPalabrasPdf.DemasiadasPaginas");

            var paginas = new List<PaginaPdf>(documento.NumberOfPages);
            long palabrasEnTotal = 0;

            foreach (var pagina in documento.GetPages())
            {
                if (paginas.Count >= LimitesLecturaPalabrasPdf.MaximoPaginas || reloj.Elapsed > LimitesLecturaPalabrasPdf.TiempoMaximo)
                    return Fallo("ExtractorPalabrasPdf.LimiteSuperado");

                var todas = pagina.GetWords(NearestNeighbourWordExtractor.Instance).ToList();

                // Texto en diagonal o vertical (marcas de agua, sellos) no forma parte de ninguna tabla,
                // pero se cuenta: una página girada entera no puede pasar por una página casi vacía.
                var horizontales = todas
                    .Where(w => w.TextOrientation == TextOrientation.Horizontal)
                    .Select(w => new PalabraPdf(w.Text, w.BoundingBox.Left, w.BoundingBox.Right, w.BoundingBox.Bottom, w.BoundingBox.Top))
                    .ToList();

                palabrasEnTotal += todas.Count;
                if (palabrasEnTotal > LimitesLecturaPalabrasPdf.MaximoPalabrasEnTotal)
                    return Fallo("ExtractorPalabrasPdf.LimiteSuperado");

                paginas.Add(new PaginaPdf(pagina.Number, horizontales, ProporcionDeImagenMayor(pagina), todas.Count - horizontales.Count));
            }

            // Un PDF truncado puede entregar menos páginas de las que declara: lo que falta es texto que no se ha visto.
            if (paginas.Count != documento.NumberOfPages)
                return Fallo("ExtractorPalabrasPdf.PaginasIncompletas");

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
