using CaeManager.Application.Common;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Application.Trabajadores.Extraccion;
using CaeManager.Domain.Common;
using Microsoft.Extensions.Logging;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace CaeManager.Infrastructure.HerramientasPdf;

/// <summary>
/// El motor de Dividir: reparte un PDF con PDFsharp. Corre solo dentro del proceso aparte (<see cref="ProcesoHerramientasPdf"/>),
/// con su límite duro de memoria y de tiempo; el proceso web usa <see cref="DivisorPdfEnProcesoAparte"/> y un test de
/// arquitectura impide llamar a este motor desde cualquier otro sitio. Esa es la garantía frente a un PDF hostil: lo que
/// PDFsharp descomprima o construya al abrirlo agota el proceso aparte, no la aplicación.
///
/// Antes de abrir se acota por tamaño, por páginas declaradas y por la estructura que se descomprimiría al cargarlo
/// (<see cref="AcotadorEstructuraPdf"/>), y después por las páginas reales. El acotador es solo un primer filtro barato:
/// no lo ve todo (arrays enormes en texto plano, o un flujo que su ventana de búsqueda no alcanza), y lo que se le escapa lo
/// para el límite del proceso aparte. Los mensajes de excepción no se registran porque pueden reproducir contenido del
/// documento.
///
/// La firma se detecta buscando <c>/ByteRange</c> en los bytes, sin validarla: solo sirve para avisar de que un tramo
/// la pierde, y una verificación real (<c>VerificadorFirmaPdfService</c>) consulta la revocación por red.
/// </summary>
public sealed class PdfSharpDivisorPdf(ILogger<PdfSharpDivisorPdf> logger)
{
    /// <summary>Abre y cierra el PDF: lo que el proceso web necesita saber antes de dividirlo.</summary>
    public Result<(int NumeroPaginas, bool EstaFirmado)> AbrirParaMedir(byte[] contenidoPdf)
    {
        var abierto = Abrir(contenidoPdf);
        if (!abierto.EsExitoso)
            return Result.Fallo<(int, bool)>(abierto.Error);

        using var pdf = abierto.Valor;
        return Result.Exito((pdf.NumeroPaginas, pdf.EstaFirmado));
    }

    /// <summary>
    /// Abre el original y escribe el tramo pedido, sin pasar de <paramref name="cota"/>. Lo que PDFsharp lance al copiar
    /// páginas de un PDF dañado es el fallo de ese tramo; la falta de memoria sigue su curso hasta el proceso aparte.
    /// </summary>
    public Result<byte[]> ExtraerTramo(byte[] contenidoPdf, int paginaInicial, int paginaFinal, long cota)
    {
        var abierto = Abrir(contenidoPdf);
        if (!abierto.EsExitoso)
            return Result.Fallo<byte[]>(abierto.Error);

        using var pdf = abierto.Valor;
        try
        {
            return pdf.ExtraerPaginas(paginaInicial, paginaFinal, cota);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogInformation("Dividir PDF no pudo extraer un tramo ({Tipo}).", ex.GetType().Name);
            return Result.Fallo<byte[]>(Error.Crear(ErroresDivisorPdf.Ilegible, "No se ha podido preparar este tramo."));
        }
    }

    public Result<PdfAbierto> Abrir(byte[] contenidoPdf)
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
            // propietario (PDFsharp lo abriría), por la misma regla que la unión. Ahora que se abre en el proceso aparte, el
            // acotador ya no es la garantía y esta regla se podría relajar; es una decisión de producto que sigue abierta.
            // Sin flujos de objetos, sí se mide y decide PDFsharp.
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

        return Result.Exito(new PdfAbierto(documento, contenidoPdf.AsSpan().IndexOf("/ByteRange"u8) >= 0));
    }

    private static Result<PdfAbierto> Fallo(string codigo, string mensaje) =>
        Result.Fallo<PdfAbierto>(Error.Crear(codigo, mensaje));

    /// <summary>PDF ya abierto con PDFsharp, dentro del proceso aparte.</summary>
    public sealed class PdfAbierto(PdfDocument documento, bool firmado) : IDisposable
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
