using CaeManager.Application.Common;
using SkiaSharp;

namespace CaeManager.Infrastructure.Firmas;

/// <summary>
/// SkiaSharp y no SixLabors.ImageSharp (licencia dividida por facturación
/// anual — misma cautela que este repo ya aplicó al descartar iText por ser
/// AGPL): MIT, y multiplataforma real en Linux sin depender de libgdiplus.
/// </summary>
public class SkiaConversorImagenSelloService : IConversorImagenSelloService
{
    // Umbral de luminosidad (0-255): píxeles más claros que esto se vuelven
    // transparentes. Calibrado para el caso común (foto de un sello sobre
    // papel blanco bajo luz normal, no un escaneo perfecto).
    private const byte UmbralLuminosidad = 235;

    // Protección contra decompression bomb (auditoría de seguridad del
    // módulo, 2026-08-30): el límite de 5 MB del comando acota los bytes
    // comprimidos, no los píxeles resultantes — un PNG/JPEG pequeño puede
    // declarar dimensiones enormes. Un sello o firma reales no necesitan
    // más resolución que esto; se comprueba con SKCodec (solo cabecera)
    // antes de decodificar los píxeles.
    private const int DimensionMaximaPixeles = 8_000;
    private const long MegapixelesMaximos = 40_000_000;

    public byte[] NormalizarAPng(byte[] imagenOriginal, bool recortarFondoClaro)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(imagenOriginal))
            ?? throw new ArgumentException("No se pudo leer la imagen: formato no soportado.", nameof(imagenOriginal));

        var infoCodec = codec.Info;
        if (infoCodec.Width <= 0 || infoCodec.Height <= 0
            || infoCodec.Width > DimensionMaximaPixeles || infoCodec.Height > DimensionMaximaPixeles
            || (long)infoCodec.Width * infoCodec.Height > MegapixelesMaximos)
        {
            throw new ArgumentException(
                $"La imagen es demasiado grande ({infoCodec.Width}x{infoCodec.Height} píxeles).", nameof(imagenOriginal));
        }

        using var original = SKBitmap.Decode(codec)
            ?? throw new ArgumentException("No se pudo leer la imagen: formato no soportado.", nameof(imagenOriginal));

        // Copia a un formato con canal alfa explícito y no premultiplicado:
        // el original puede venir sin alfa (p. ej. un JPEG), y SetPixel más
        // abajo necesita poder escribir transparencia píxel a píxel.
        var info = new SKImageInfo(original.Width, original.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
            canvas.DrawBitmap(original, 0, 0);

        if (recortarFondoClaro)
            RecortarFondoClaro(bitmap);

        using var imagen = SKImage.FromBitmap(bitmap);
        using var datos = imagen.Encode(SKEncodedImageFormat.Png, 100);
        return datos.ToArray();
    }

    private static void RecortarFondoClaro(SKBitmap bitmap)
    {
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                var luminosidad = (byte)((pixel.Red + pixel.Green + pixel.Blue) / 3);
                if (luminosidad >= UmbralLuminosidad)
                    bitmap.SetPixel(x, y, new SKColor(pixel.Red, pixel.Green, pixel.Blue, 0));
            }
        }
    }
}
