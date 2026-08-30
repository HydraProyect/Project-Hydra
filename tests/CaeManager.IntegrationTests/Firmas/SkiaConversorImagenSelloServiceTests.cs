using CaeManager.Infrastructure.Firmas;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CaeManager.IntegrationTests.Firmas;

public class SkiaConversorImagenSelloServiceTests
{
    /// <summary>Imagen de prueba: mitad izquierda blanca (fondo), mitad derecha negra (el "sello"). Sin canal alfa, como un JPEG real.</summary>
    private static byte[] CrearImagenDePrueba()
    {
        var info = new SKImageInfo(10, 10, SKColorType.Rgb888x, SKAlphaType.Opaque);
        using var bitmap = new SKBitmap(info);
        for (var y = 0; y < 10; y++)
            for (var x = 0; x < 10; x++)
                bitmap.SetPixel(x, y, x < 5 ? SKColors.White : SKColors.Black);

        using var imagen = SKImage.FromBitmap(bitmap);
        using var datos = imagen.Encode(SKEncodedImageFormat.Png, 100);
        return datos.ToArray();
    }

    private static SKBitmap DecodificarResultado(byte[] png) => SKBitmap.Decode(png);

    [Fact]
    public void NormalizarAPng_produce_un_png_valido_del_mismo_tamano()
    {
        var servicio = new SkiaConversorImagenSelloService();

        var resultado = servicio.NormalizarAPng(CrearImagenDePrueba(), recortarFondoClaro: false);

        using var bitmap = DecodificarResultado(resultado);
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().Be(10);
        bitmap.Height.Should().Be(10);
    }

    [Fact]
    public void NormalizarAPng_sin_recorte_mantiene_el_fondo_opaco()
    {
        var servicio = new SkiaConversorImagenSelloService();

        var resultado = servicio.NormalizarAPng(CrearImagenDePrueba(), recortarFondoClaro: false);

        using var bitmap = DecodificarResultado(resultado);
        bitmap.GetPixel(1, 1).Alpha.Should().Be(255);
    }

    [Fact]
    public void NormalizarAPng_con_recorte_vuelve_transparente_el_fondo_claro_pero_no_el_sello()
    {
        var servicio = new SkiaConversorImagenSelloService();

        var resultado = servicio.NormalizarAPng(CrearImagenDePrueba(), recortarFondoClaro: true);

        using var bitmap = DecodificarResultado(resultado);
        bitmap.GetPixel(1, 1).Alpha.Should().Be(0, "el píxel blanco de fondo debe volverse transparente");
        bitmap.GetPixel(8, 8).Alpha.Should().Be(255, "el píxel negro del sello debe seguir opaco");
    }

    /// <summary>
    /// Protección contra decompression bomb (auditoría de seguridad del
    /// módulo, 2026-08-30): una imagen cuyo ancho declarado supera el límite
    /// se rechaza ANTES de decodificar los píxeles. Se usa una imagen muy
    /// alargada (ancho por encima del límite, un solo píxel de alto) para
    /// que el propio test no tenga que alojar los cientos de MB que una
    /// imagen cuadrada igual de ancha supondría — lo que se comprueba es que
    /// SKCodec.Create (solo cabecera) basta para rechazarla sin decodificar.
    /// </summary>
    [Fact]
    public void NormalizarAPng_rechaza_una_imagen_con_ancho_excesivo()
    {
        var servicio = new SkiaConversorImagenSelloService();
        var info = new SKImageInfo(9_000, 1, SKColorType.Rgb888x, SKAlphaType.Opaque);
        using var bitmap = new SKBitmap(info);
        using var imagen = SKImage.FromBitmap(bitmap);
        using var datos = imagen.Encode(SKEncodedImageFormat.Png, 100);

        var accion = () => servicio.NormalizarAPng(datos.ToArray(), recortarFondoClaro: false);

        accion.Should().Throw<ArgumentException>();
    }
}
