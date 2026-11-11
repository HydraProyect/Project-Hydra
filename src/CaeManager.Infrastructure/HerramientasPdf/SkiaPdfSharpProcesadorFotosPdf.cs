using CaeManager.Application.HerramientasPdf;
using CaeManager.Domain.Common;
using Microsoft.Extensions.Logging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SkiaSharp;

namespace CaeManager.Infrastructure.HerramientasPdf;

/// <summary>
/// Decodifica las fotos con SkiaSharp (MIT, ya presente en la solución para los sellos) y arma el PDF con PDFsharp.
///
/// Cada foto se decodifica, se orienta, se reduce si pasa del lado máximo y se REcodifica como JPEG sobre un lienzo
/// blanco. Ese paso es el que quita los metadatos: PDFsharp incrusta los bytes de un JPEG tal cual, de modo que meter el
/// original llevaría su EXIF (GPS, modelo de cámara, fecha, miniatura) dentro del PDF. El codificador de Skia solo
/// escribe lo que se le pide, y no se le pide nada.
///
/// Las dimensiones se leen de la cabecera ANTES de decodificar y se comparan con los topes de
/// <see cref="LimitesFotoAPdf"/> aunque el comando ya lo hiciera: son las que usa el decodificador, y un fichero que
/// engañe a un lector de cabeceras no tiene por qué engañar al otro. No se usa disco: todo ocurre en memoria. Los
/// mensajes de excepción no se registran, porque pueden reproducir contenido del fichero.
/// </summary>
public sealed class SkiaPdfSharpProcesadorFotosPdf(ILogger<SkiaPdfSharpProcesadorFotosPdf> logger) : IProcesadorFotosPdf
{
    private const int CalidadJpeg = 90;

    /// <summary>5 mm de margen en cada lado de la página, para que una impresora doméstica no recorte la foto.</summary>
    private const double MargenPuntos = 14.17;

    public Result<byte[]> ComponerPdf(IReadOnlyList<byte[]> fotos, CancellationToken cancellationToken)
    {
        if (fotos.Count == 0)
            return Fallo(ErroresFotoAPdf.SinFotos, "No hay fotos.");

        using var documento = new PdfDocument();
        for (var i = 0; i < fotos.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var normalizada = Normalizar(fotos[i], LimitesFotoAPdf.LadoMaximoEnPdfPixeles);
            if (normalizada.EsFallido)
                return Fallo(normalizada.Error.Codigo, $"Foto {i + 1}: {normalizada.Error.Mensaje}");

            AnadirPagina(documento, normalizada.Valor);
        }

        using var salida = new MemoryStream();
        documento.Save(salida, closeStream: false);
        return Result.Exito(salida.ToArray());
    }

    public Result<byte[]> CrearMiniatura(byte[] foto, int ladoMaximoPixeles, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizada = Normalizar(foto, ladoMaximoPixeles);
        return normalizada.EsFallido ? Result.Fallo<byte[]>(normalizada.Error) : Result.Exito(normalizada.Valor.Jpeg);
    }

    /// <summary>Foto ya orientada, reducida y sin metadatos.</summary>
    public readonly record struct FotoNormalizada(byte[] Jpeg, int Ancho, int Alto);

    /// <summary>
    /// Decodifica, aplica la orientación EXIF a los píxeles, reduce al lado máximo (nunca amplía) y recodifica como JPEG.
    /// Público para que un test compruebe píxeles y metadatos sin pasar por el PDF.
    /// </summary>
    public Result<FotoNormalizada> Normalizar(byte[] foto, int ladoMaximoPixeles)
    {
        try
        {
            using var datos = SKData.CreateCopy(foto);
            using var codec = SKCodec.Create(datos);
            if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png))
                return FalloNormalizacion(ErroresFotoAPdf.FormatoNoSoportado, "Solo se admiten fotos JPEG y PNG.");

            var info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0
                || info.Width > LimitesFotoAPdf.MaximoLadoPixeles || info.Height > LimitesFotoAPdf.MaximoLadoPixeles
                || (long)info.Width * info.Height > LimitesFotoAPdf.MaximoPixeles)
                return FalloNormalizacion(ErroresFotoAPdf.DemasiadosPixeles, "La foto declara más píxeles de los admitidos.");

            var origen = codec.EncodedOrigin;
            using var bitmap = SKBitmap.Decode(codec, new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (bitmap is null)
                return FalloNormalizacion(ErroresFotoAPdf.FotoIlegible, "No hemos podido leer esta foto.");

            var escala = Math.Min(1.0, (double)Math.Max(1, ladoMaximoPixeles) / Math.Max(info.Width, info.Height));
            var ancho = Math.Max(1, (int)Math.Round(info.Width * escala));
            var alto = Math.Max(1, (int)Math.Round(info.Height * escala));
            var girada = EsGiroDeCuartoDeVuelta(origen);
            var anchoSalida = girada ? alto : ancho;
            var altoSalida = girada ? ancho : alto;

            using var destino = new SKBitmap(new SKImageInfo(anchoSalida, altoSalida, SKColorType.Rgba8888, SKAlphaType.Opaque));
            using (var lienzo = new SKCanvas(destino))
            {
                lienzo.Clear(SKColors.White);
                lienzo.SetMatrix(MatrizDeOrientacion(origen, info.Width, info.Height, escala));
                using var imagen = SKImage.FromBitmap(bitmap);
                lienzo.DrawImage(imagen, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            }

            using var flujo = new SKDynamicMemoryWStream();
            using var pixeles = destino.PeekPixels();
            var opciones = new SKJpegEncoderOptions(CalidadJpeg, SKJpegEncoderDownsample.Downsample444, SKJpegEncoderAlphaOption.Ignore);
            if (!pixeles.Encode(flujo, opciones))
                return FalloNormalizacion(ErroresFotoAPdf.NoSePudoGenerar, "No hemos podido procesar esta foto.");

            using var jpeg = flujo.DetachAsData();
            return Result.Exito(new FotoNormalizada(jpeg.ToArray(), anchoSalida, altoSalida));
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            logger.LogInformation("Foto no procesable para el PDF ({Tipo}).", ex.GetType().Name);
            return FalloNormalizacion(ErroresFotoAPdf.FotoIlegible, "No hemos podido leer esta foto.");
        }
    }

    private static bool EsGiroDeCuartoDeVuelta(SKEncodedOrigin origen) =>
        origen is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

    /// <summary>
    /// Lleva un píxel (x, y) de la foto tal como se guardó a su sitio en la imagen derecha, ya con la escala. Los ocho
    /// valores son los de la etiqueta EXIF Orientation: 1 normal, 2 espejo horizontal, 3 giro de 180°, 4 espejo vertical,
    /// 5 transpuesta, 6 giro de 90° a la derecha, 7 transversal y 8 giro de 90° a la izquierda. Fuera de ellos, no se gira.
    /// </summary>
    private static SKMatrix MatrizDeOrientacion(SKEncodedOrigin origen, int ancho, int alto, double escala)
    {
        // destinoX = a*x + c*y + tx; destinoY = b*x + d*y + ty
        var (a, b, c, d, tx, ty) = origen switch
        {
            SKEncodedOrigin.TopRight => (-1, 0, 0, 1, ancho, 0),
            SKEncodedOrigin.BottomRight => (-1, 0, 0, -1, ancho, alto),
            SKEncodedOrigin.BottomLeft => (1, 0, 0, -1, 0, alto),
            SKEncodedOrigin.LeftTop => (0, 1, 1, 0, 0, 0),
            SKEncodedOrigin.RightTop => (0, 1, -1, 0, alto, 0),
            SKEncodedOrigin.RightBottom => (0, -1, -1, 0, alto, ancho),
            SKEncodedOrigin.LeftBottom => (0, -1, 1, 0, 0, ancho),
            _ => (1, 0, 0, 1, 0, 0),
        };

        var s = (float)escala;
        return new SKMatrix
        {
            ScaleX = a * s,
            SkewY = b * s,
            SkewX = c * s,
            ScaleY = d * s,
            TransX = tx * s,
            TransY = ty * s,
            Persp2 = 1,
        };
    }

    /// <summary>Página A4, derecha o apaisada según la foto, con la foto entera y centrada, sin recortar ni deformar.</summary>
    private static void AnadirPagina(PdfDocument documento, FotoNormalizada foto)
    {
        var pagina = documento.AddPage();
        pagina.Size = PdfSharp.PageSize.A4;
        pagina.Orientation = foto.Ancho > foto.Alto ? PdfSharp.PageOrientation.Landscape : PdfSharp.PageOrientation.Portrait;

        using var graficos = XGraphics.FromPdfPage(pagina);
        using var flujo = new MemoryStream(foto.Jpeg, writable: false);
        using var imagen = XImage.FromStream(flujo);

        var anchoDisponible = pagina.Width.Point - 2 * MargenPuntos;
        var altoDisponible = pagina.Height.Point - 2 * MargenPuntos;
        var factor = Math.Min(anchoDisponible / foto.Ancho, altoDisponible / foto.Alto);
        var anchoFinal = foto.Ancho * factor;
        var altoFinal = foto.Alto * factor;

        graficos.DrawImage(imagen,
            MargenPuntos + (anchoDisponible - anchoFinal) / 2,
            MargenPuntos + (altoDisponible - altoFinal) / 2,
            anchoFinal, altoFinal);
    }

    private static Result<byte[]> Fallo(string codigo, string mensaje) => Result.Fallo<byte[]>(Error.Crear(codigo, mensaje));

    private static Result<FotoNormalizada> FalloNormalizacion(string codigo, string mensaje) =>
        Result.Fallo<FotoNormalizada>(Error.Crear(codigo, mensaje));
}
