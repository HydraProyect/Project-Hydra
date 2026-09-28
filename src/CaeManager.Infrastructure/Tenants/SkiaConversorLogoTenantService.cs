using CaeManager.Application.Common;
using SkiaSharp;

namespace CaeManager.Infrastructure.Tenants;

/// <summary>
/// Conversor del logo del Tenant (contrato del selector de Tenant, § 4.1.2 e invariante I10). Parte
/// del precedente del sello (<c>SkiaConversorImagenSelloService</c>) con límites propios:
/// <list type="bullet">
/// <item>solo PNG y JPEG, decidido por la cabecera (<see cref="SKCodec.EncodedFormat"/>); SVG, GIF,
/// WebP y el resto se rechazan: un SVG servido es contenido activo y no hay saneador;</item>
/// <item>4096 px por lado, leído en la cabecera ANTES de decodificar: una bomba de descompresión (PNG
/// diminuto que declara dimensiones enormes) no llega a reservar memoria;</item>
/// <item>concurrencia acotada por proceso (<see cref="ConversionesSimultaneas"/>): N subidas
/// simultáneas no multiplican la memoria de decodificación (revisión Codex C7);</item>
/// <item>la salida se dibuja en un lienzo nuevo de 256 × 256 sin espacio de color y se codifica desde
/// ahí: ningún chunk de texto, EXIF ni perfil ICC del original sobrevive.</item>
/// </list>
/// </summary>
public class SkiaConversorLogoTenantService : IConversorLogoTenantService
{
    public const int DimensionMaximaPixeles = 4_096;
    public const int ConversionesSimultaneas = 2;

    private static readonly SemaphoreSlim Turnos = new(ConversionesSimultaneas, ConversionesSimultaneas);

    public byte[] ConvertirAPng(byte[] imagenOriginal)
    {
        ArgumentNullException.ThrowIfNull(imagenOriginal);

        Turnos.Wait();
        try
        {
            return Convertir(imagenOriginal);
        }
        finally
        {
            Turnos.Release();
        }
    }

    private static byte[] Convertir(byte[] imagenOriginal)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(imagenOriginal))
            ?? throw new LogoTenantNoAdmitidoException(
                MotivoLogoNoAdmitido.FormatoNoAdmitido, "Formato no admitido (PNG, JPG).");

        if (codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg))
            throw new LogoTenantNoAdmitidoException(
                MotivoLogoNoAdmitido.FormatoNoAdmitido, "Formato no admitido (PNG, JPG).");

        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0
            || info.Width > DimensionMaximaPixeles || info.Height > DimensionMaximaPixeles)
            throw new LogoTenantNoAdmitidoException(
                MotivoLogoNoAdmitido.DimensionesExcesivas,
                $"La imagen es demasiado grande (máximo {DimensionMaximaPixeles} × {DimensionMaximaPixeles} píxeles).");

        var infoDecodificada = new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var original = new SKBitmap(infoDecodificada);
        var resultado = codec.GetPixels(infoDecodificada, original.GetPixels());
        // Solo Success: un PNG/JPEG truncado con cabecera válida no puede sustituir el logo por una
        // imagen a medio decodificar (IncompleteInput).
        if (resultado != SKCodecResult.Success)
            throw new LogoTenantNoAdmitidoException(
                MotivoLogoNoAdmitido.NoDecodificable, "No se pudo leer la imagen.");

        const int lado = IConversorLogoTenantService.LadoPixeles;
        var escala = Math.Min((float)lado / info.Width, (float)lado / info.Height);
        var ancho = info.Width * escala;
        var alto = info.Height * escala;
        var destino = SKRect.Create((lado - ancho) / 2f, (lado - alto) / 2f, ancho, alto);

        // Sin espacio de color: el codificador no escribe iCCP ni sRGB heredados del original.
        var infoSalida = new SKImageInfo(lado, lado, SKColorType.Rgba8888, SKAlphaType.Premul, colorspace: null);
        using var superficie = SKSurface.Create(infoSalida)
            ?? throw new LogoTenantNoAdmitidoException(MotivoLogoNoAdmitido.NoDecodificable, "No se pudo leer la imagen.");
        var lienzo = superficie.Canvas;
        lienzo.Clear(SKColors.Transparent);
        using (var fuente = SKImage.FromBitmap(original))
            lienzo.DrawImage(fuente, destino, new SKSamplingOptions(SKCubicResampler.Mitchell));
        lienzo.Flush();

        using var imagen = superficie.Snapshot();
        using var datos = imagen.Encode(SKEncodedImageFormat.Png, 100);
        return datos.ToArray();
    }
}
