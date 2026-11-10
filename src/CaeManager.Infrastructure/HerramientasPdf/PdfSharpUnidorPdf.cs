using CaeManager.Application.Common;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Application.Trabajadores.Extraccion;
using CaeManager.Domain.Common;
using Microsoft.Extensions.Logging;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace CaeManager.Infrastructure.HerramientasPdf;

/// <summary>
/// Une PDF con PDFsharp, en este proceso. Los PDF los sube un tercero, así que se acotan antes de abrirlos por tamaño y por
/// páginas declaradas, y después por las reales y por el total del unido. Los mensajes de excepción no se registran porque
/// pueden reproducir contenido del documento.
///
/// Un PDF unido es un documento nuevo: copia las páginas y no conserva la firma electrónica de ninguno. La firma se detecta
/// buscando <c>/ByteRange</c> en los bytes, sin validarla, igual que <see cref="PdfSharpDivisorPdf"/>: solo sirve para
/// avisar de que se pierde.
/// </summary>
public sealed class PdfSharpUnidorPdf(ILogger<PdfSharpUnidorPdf> logger) : IUnidorPdf
{
    public Result<InfoPdfParaUnir> Inspeccionar(byte[] contenidoPdf)
    {
        var abierto = Abrir(contenidoPdf);
        if (!abierto.EsExitoso)
            return Result.Fallo<InfoPdfParaUnir>(abierto.Error);

        using var documento = abierto.Valor;
        return Result.Exito(new InfoPdfParaUnir(documento.PageCount, EstaFirmado(contenidoPdf)));
    }

    public Result<PdfUnido> Unir(IReadOnlyList<byte[]> pdfs)
    {
        var origenes = new List<PdfDocument>(pdfs.Count);
        try
        {
            using var unido = new PdfDocument();
            var paginas = 0;

            foreach (var contenido in pdfs)
            {
                var abierto = Abrir(contenido);
                if (!abierto.EsExitoso)
                    return Result.Fallo<PdfUnido>(abierto.Error);

                // Se conserva abierto hasta guardar: las páginas importadas pueden seguir leyendo de su origen.
                origenes.Add(abierto.Valor);
                paginas += abierto.Valor.PageCount;
                if (paginas > LimitesUnionPdf.MaximoPaginasTotales)
                    return Fallo<PdfUnido>(ErroresUnirPdf.DemasiadasPaginas, "El PDF unido tiene demasiadas páginas.");

                foreach (var pagina in abierto.Valor.Pages)
                    unido.AddPage(pagina);
            }

            using var salida = new MemoryStream();
            unido.Save(salida, closeStream: false);
            return Result.Exito(new PdfUnido(salida.ToArray(), paginas));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogInformation("No se pudo unir un PDF ({Tipo}).", ex.GetType().Name);
            return Fallo<PdfUnido>(ErroresUnirPdf.Ilegible, "No hemos podido unir estos PDF.");
        }
        finally
        {
            foreach (var origen in origenes)
                origen.Dispose();
        }
    }

    private Result<PdfDocument> Abrir(byte[] contenidoPdf)
    {
        if (contenidoPdf.Length == 0)
            return Fallo<PdfDocument>(ErroresUnirPdf.Ilegible, "El PDF está vacío.");

        if (contenidoPdf.Length > LimitesUnionPdf.TamanoMaximoTotalBytes)
            return Fallo<PdfDocument>(ErroresUnirPdf.DemasiadoGrande, "El PDF supera el tamaño máximo.");

        if (LectorRecuentoPaginasPdfSinAbrir.IntentarLeerRecuentoDePaginasSinAbrir(contenidoPdf) is > LimitesUnionPdf.MaximoPaginasTotales)
            return Fallo<PdfDocument>(ErroresUnirPdf.DemasiadasPaginas, "El PDF tiene demasiadas páginas.");

        PdfDocument documento;
        try
        {
            documento = PdfReader.Open(new MemoryStream(contenidoPdf, writable: false), PdfDocumentOpenMode.Import);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var cifrado = contenidoPdf.AsSpan().IndexOf("/Encrypt"u8) >= 0;
            logger.LogInformation("PDF no legible para unir ({Tipo}, cifrado: {Cifrado}).", ex.GetType().Name, cifrado);

            return cifrado
                ? Fallo<PdfDocument>(ErroresUnirPdf.ConContrasena, "El PDF tiene contraseña.")
                : Fallo<PdfDocument>(ErroresUnirPdf.Ilegible, "No hemos podido leer este PDF.");
        }

        if (documento.PageCount > LimitesUnionPdf.MaximoPaginasTotales)
        {
            documento.Dispose();
            return Fallo<PdfDocument>(ErroresUnirPdf.DemasiadasPaginas, "El PDF tiene demasiadas páginas.");
        }

        return Result.Exito(documento);
    }

    private static bool EstaFirmado(byte[] contenidoPdf) => contenidoPdf.AsSpan().IndexOf("/ByteRange"u8) >= 0;

    private static Result<T> Fallo<T>(string codigo, string mensaje) => Result.Fallo<T>(Error.Crear(codigo, mensaje));
}
