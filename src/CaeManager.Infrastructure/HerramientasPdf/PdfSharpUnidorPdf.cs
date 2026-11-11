using CaeManager.Application.Common;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Domain.Common;
using Microsoft.Extensions.Logging;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace CaeManager.Infrastructure.HerramientasPdf;

/// <summary>
/// Une PDF con PDFsharp, en este proceso. Los PDF los sube un tercero, así que se acotan antes de abrirlos: por tamaño, por
/// páginas declaradas y por el coste de su estructura (<see cref="AcotadorEstructuraPdf"/>: objetos declarados y lo que se
/// descomprime de flujos de objetos y tablas comprimidas, que PDFsharp carga entero al abrir y no deja interrumpir). Después,
/// por las páginas reales y por el total del unido. Los mensajes de excepción no se registran porque pueden reproducir
/// contenido del documento.
///
/// Un PDF unido es un documento nuevo: copia las páginas y no conserva la firma electrónica de ninguno. La firma se detecta
/// por la estructura del PDF ya abierto, sin validarla: un campo <c>/FT /Sig</c> con valor, o un diccionario de firma
/// (<c>/Type /Sig</c> o <c>/DocTimeStamp</c>) con <c>/ByteRange</c>. PDFsharp decodifica los nombres (<c>/Byte#52ange</c>
/// es <c>/ByteRange</c>), así que un nombre codificado no esconde la firma, y un comentario con esas letras no la finge.
/// Solo sirve para avisar de que se pierde.
/// </summary>
public sealed class PdfSharpUnidorPdf(ILogger<PdfSharpUnidorPdf> logger) : IUnidorPdf
{
    public Result<InfoPdfParaUnir> Inspeccionar(byte[] contenidoPdf)
    {
        var abierto = Abrir(contenidoPdf);
        if (!abierto.EsExitoso)
            return Result.Fallo<InfoPdfParaUnir>(abierto.Error);

        using var documento = abierto.Valor.Documento;
        return Result.Exito(new InfoPdfParaUnir(documento.PageCount, EstaFirmado(documento), abierto.Valor.BytesEstructura));
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
                var origen = abierto.Valor.Documento;
                origenes.Add(origen);
                paginas += origen.PageCount;
                if (paginas > LimitesUnionPdf.MaximoPaginasTotales)
                    return Fallo<PdfUnido>(ErroresUnirPdf.DemasiadasPaginas, "El PDF unido tiene demasiadas páginas.");

                foreach (var pagina in origen.Pages)
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

    private Result<(PdfDocument Documento, long BytesEstructura)> Abrir(byte[] contenidoPdf)
    {
        if (contenidoPdf.Length == 0)
            return Fallo<(PdfDocument, long)>(ErroresUnirPdf.Ilegible, "El PDF está vacío.");

        if (contenidoPdf.Length > LimitesUnionPdf.TamanoMaximoTotalBytes)
            return Fallo<(PdfDocument, long)>(ErroresUnirPdf.DemasiadoGrande, "El PDF supera el tamaño máximo.");

        if (LectorRecuentoPaginasPdfSinAbrir.IntentarLeerRecuentoDePaginasSinAbrir(contenidoPdf) is > LimitesUnionPdf.MaximoPaginasTotales)
            return Fallo<(PdfDocument, long)>(ErroresUnirPdf.DemasiadasPaginas, "El PDF tiene demasiadas páginas.");

        var cifrado = contenidoPdf.AsSpan().IndexOf("/Encrypt"u8) >= 0;

        // Antes de PDFsharp, que carga todos los objetos y no deja acotar ni cortar ese trabajo.
        long? estructura;
        try
        {
            estructura = AcotadorEstructuraPdf.Medir(contenidoPdf);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogInformation("PDF no medible para unir ({Tipo}).", ex.GetType().Name);
            estructura = null;
        }

        if (estructura is not { } bytesEstructura)
        {
            // Un PDF cifrado no se deja medir (sus flujos de objetos van cifrados) y tampoco se abriría sin la contraseña.
            logger.LogInformation("PDF rechazado antes de abrir para unir: estructura fuera de los topes (cifrado: {Cifrado}).", cifrado);
            return cifrado
                ? Fallo<(PdfDocument, long)>(ErroresUnirPdf.ConContrasena, "El PDF tiene contraseña.")
                : Fallo<(PdfDocument, long)>(ErroresUnirPdf.DemasiadoComplejo, "El PDF es demasiado complejo para unirlo.");
        }

        PdfDocument documento;
        try
        {
            documento = PdfReader.Open(new MemoryStream(contenidoPdf, writable: false), PdfDocumentOpenMode.Import);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogInformation("PDF no legible para unir ({Tipo}, cifrado: {Cifrado}).", ex.GetType().Name, cifrado);

            return cifrado
                ? Fallo<(PdfDocument, long)>(ErroresUnirPdf.ConContrasena, "El PDF tiene contraseña.")
                : Fallo<(PdfDocument, long)>(ErroresUnirPdf.Ilegible, "No hemos podido leer este PDF.");
        }

        if (documento.PageCount > LimitesUnionPdf.MaximoPaginasTotales)
        {
            documento.Dispose();
            return Fallo<(PdfDocument, long)>(ErroresUnirPdf.DemasiadasPaginas, "El PDF tiene demasiadas páginas.");
        }

        return Result.Exito((documento, bytesEstructura));
    }

    /// <summary>
    /// Recorre los objetos ya cargados (acotados por <see cref="AcotadorEstructuraPdf"/>): firmado si hay un campo de firma
    /// con valor o un diccionario de firma con su rango firmado.
    /// </summary>
    private static bool EstaFirmado(PdfDocument documento)
    {
        foreach (var objeto in documento.Internals.GetAllObjects())
        {
            if (objeto is not PdfDictionary diccionario)
                continue;

            if (diccionario.Elements.GetName("/FT") == "/Sig" && diccionario.Elements.GetDictionary("/V") is { } valor
                && valor.Elements.ContainsKey("/ByteRange"))
                return true;

            if (diccionario.Elements.GetName("/Type") is "/Sig" or "/DocTimeStamp" && diccionario.Elements.ContainsKey("/ByteRange"))
                return true;
        }

        return false;
    }

    private static Result<T> Fallo<T>(string codigo, string mensaje) => Result.Fallo<T>(Error.Crear(codigo, mensaje));
}
