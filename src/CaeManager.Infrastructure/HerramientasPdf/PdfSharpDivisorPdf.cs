using CaeManager.Application.Common;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Application.Trabajadores.Extraccion;
using CaeManager.Domain.Common;
using Microsoft.Extensions.Logging;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace CaeManager.Infrastructure.HerramientasPdf;

/// <summary>
/// Reparte un PDF con PDFsharp, en este proceso. El PDF lo sube un tercero, así que se acota antes de abrirlo por
/// tamaño y por páginas declaradas, y después por las reales. Los mensajes de excepción no se registran porque
/// pueden reproducir contenido del documento.
///
/// La firma se detecta buscando <c>/ByteRange</c> en los bytes, sin validarla: solo sirve para avisar de que un tramo
/// la pierde, y una verificación real (<c>VerificadorFirmaPdfService</c>) consulta la revocación por red.
/// </summary>
public sealed class PdfSharpDivisorPdf(ILogger<PdfSharpDivisorPdf> logger) : IDivisorPdf
{
    public Result<IPdfParaDividir> Abrir(byte[] contenidoPdf)
    {
        if (contenidoPdf.Length == 0)
            return Fallo(ErroresDivisorPdf.Ilegible, "El PDF está vacío.");

        if (contenidoPdf.Length > LimitesDivisionPdf.TamanoMaximoOriginalBytes)
            return Fallo(ErroresDivisorPdf.DemasiadoGrande, "El PDF supera el tamaño máximo.");

        if (LectorRecuentoPaginasPdfSinAbrir.IntentarLeerRecuentoDePaginasSinAbrir(contenidoPdf) is > LimitesLecturaPalabrasPdf.MaximoPaginas)
            return Fallo(ErroresDivisorPdf.DemasiadasPaginas, "El PDF tiene demasiadas páginas.");

        PdfDocument documento;
        try
        {
            documento = PdfReader.Open(new MemoryStream(contenidoPdf, writable: false), PdfDocumentOpenMode.Import);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var cifrado = contenidoPdf.AsSpan().IndexOf("/Encrypt"u8) >= 0;
            logger.LogInformation("PDF no legible para dividir ({Tipo}, cifrado: {Cifrado}).", ex.GetType().Name, cifrado);

            return cifrado
                ? Fallo(ErroresDivisorPdf.ConContrasena, "El PDF tiene contraseña.")
                : Fallo(ErroresDivisorPdf.Ilegible, "No hemos podido leer este PDF.");
        }

        if (documento.PageCount > LimitesLecturaPalabrasPdf.MaximoPaginas)
        {
            documento.Dispose();
            return Fallo(ErroresDivisorPdf.DemasiadasPaginas, "El PDF tiene demasiadas páginas.");
        }

        return Result.Exito<IPdfParaDividir>(new PdfAbierto(documento, contenidoPdf.AsSpan().IndexOf("/ByteRange"u8) >= 0));
    }

    private static Result<IPdfParaDividir> Fallo(string codigo, string mensaje) =>
        Result.Fallo<IPdfParaDividir>(Error.Crear(codigo, mensaje));

    private sealed class PdfAbierto(PdfDocument documento, bool firmado) : IPdfParaDividir
    {
        public int NumeroPaginas => documento.PageCount;

        public bool EstaFirmado => firmado;

        public Result<byte[]> ExtraerPaginas(int paginaInicial, int paginaFinal)
        {
            if (paginaInicial < 1 || paginaFinal < paginaInicial || paginaFinal > documento.PageCount)
                return Result.Fallo<byte[]>(Error.Crear(ErroresDivisorPdf.RangoInvalido, "Las páginas no están en el PDF."));

            using var tramo = new PdfDocument();
            for (var indice = paginaInicial - 1; indice < paginaFinal; indice++)
                tramo.AddPage(documento.Pages[indice]);

            using var salida = new MemoryStream();
            tramo.Save(salida, closeStream: false);
            return Result.Exito(salida.ToArray());
        }

        public void Dispose() => documento.Dispose();
    }
}
