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
/// tamaño, por páginas declaradas y por la estructura que se descomprimiría al cargarlo (<see cref="AcotadorEstructuraPdf"/>),
/// y después por las páginas reales. Queda fuera lo que el acotador no mide (por ejemplo, arrays enormes en texto plano
/// dentro del tope de tamaño), que en la unión para el proceso aparte y aquí no. Los mensajes de excepción no se registran porque
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

        var cifrado = contenidoPdf.AsSpan().IndexOf("/Encrypt"u8) >= 0;

        // Antes de PDFsharp, que carga todos los objetos al abrir y no deja acotar ni cortar ese trabajo: los mismos topes
        // de estructura que la unión. Acota también todo lo que parte de esta apertura (vistas, guardar, descargar).
        long? estructura;
        try
        {
            estructura = AcotadorEstructuraPdf.Medir(contenidoPdf);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogInformation("PDF no medible para dividir ({Tipo}).", ex.GetType().Name);
            estructura = null;
        }

        if (estructura is null)
        {
            // Un PDF cifrado no se deja medir: sus flujos de objetos van cifrados. Se rechaza aunque solo lleve contraseña de
            // propietario (PDFsharp lo abriría), porque dejarlo pasar sin medir convertiría «/Encrypt» en la forma de saltarse
            // el acotador; es la misma regla que la unión. Sin flujos de objetos, sí se mide y decide PDFsharp.
            logger.LogInformation("PDF rechazado antes de abrir para dividir: estructura fuera de los topes (cifrado: {Cifrado}).", cifrado);
            return cifrado
                ? Fallo(ErroresDivisorPdf.ConContrasena, "El PDF tiene contraseña.")
                : Fallo(ErroresDivisorPdf.DemasiadoComplejo, "El PDF es demasiado complejo para dividirlo.");
        }

        PdfDocument documento;
        try
        {
            documento = PdfReader.Open(new MemoryStream(contenidoPdf, writable: false), PdfDocumentOpenMode.Import);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
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

        public Result<byte[]> ExtraerPaginas(int paginaInicial, int paginaFinal, long tamanoMaximoBytes)
        {
            if (paginaInicial < 1 || paginaFinal < paginaInicial || paginaFinal > documento.PageCount)
                return Result.Fallo<byte[]>(Error.Crear(ErroresDivisorPdf.RangoInvalido, "Las páginas no están en el PDF."));

            using var tramo = new PdfDocument();
            for (var indice = paginaInicial - 1; indice < paginaFinal; indice++)
                tramo.AddPage(documento.Pages[indice]);

            return GuardarConCota(salida => tramo.Save(salida, closeStream: false), tamanoMaximoBytes);
        }

        public void Dispose() => documento.Dispose();
    }

    /// <summary>
    /// Guarda en un <see cref="FlujoMemoriaAcotado"/> y falla cerrado si alguna escritura habría pasado la cota, tanto si
    /// el guardado lanza como si se traga la excepción y termina: lo que quedó en el flujo no es un tramo completo.
    /// </summary>
    internal static Result<byte[]> GuardarConCota(Action<Stream> guardar, long cota)
    {
        using var salida = new FlujoMemoriaAcotado(cota);
        try
        {
            guardar(salida);
        }
        catch (Exception) when (salida.CotaSuperada)
        {
            // Lo que lance PDFsharp al cortarle la escritura, la propia cota o una excepción suya que la envuelva.
            return TramoDemasiadoGrande();
        }

        return salida.CotaSuperada ? TramoDemasiadoGrande() : Result.Exito(salida.ToArray());
    }

    private static Result<byte[]> TramoDemasiadoGrande() =>
        Result.Fallo<byte[]>(Error.Crear(ErroresDivisorPdf.DemasiadoGrande, "El tramo supera el tamaño que se puede preparar."));
}
