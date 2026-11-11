using CaeManager.Application.HerramientasPdf;
using CaeManager.Infrastructure.HerramientasPdf;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using Xunit;

namespace CaeManager.Web.Tests.HerramientasPdf;

/// <summary>
/// El procesador de Foto a PDF sobre imágenes REALES (Application no puede decodificar): orientación EXIF aplicada a los
/// píxeles, EXIF ausente del resultado y límites de decodificación. Cada propiedad lleva su comprobación de sensibilidad:
/// el fixture tiene de verdad el EXIF y el instrumento que busca el GPS lo encuentra en el original.
/// </summary>
public sealed class ProcesadorFotosPdfTests
{
    private const string CanarioGps = "GPSLatitude=41.3851N;GPSLongitude=2.1734E;Make=TalvegCam";

    private readonly SkiaPdfSharpProcesadorFotosPdf _procesador = new(NullLogger<SkiaPdfSharpProcesadorFotosPdf>.Instance);

    /// <summary>Mitad izquierda roja y mitad derecha azul: cualquier giro o espejo cambia dónde queda el rojo.</summary>
    private static byte[] FotoBicolor(int ancho, int alto, SKEncodedImageFormat formato = SKEncodedImageFormat.Jpeg)
    {
        using var bitmap = new SKBitmap(ancho, alto);
        using (var lienzo = new SKCanvas(bitmap))
        {
            lienzo.Clear(SKColors.Blue);
            using var rojo = new SKPaint { Color = SKColors.Red };
            lienzo.DrawRect(0, 0, ancho / 2f, alto, rojo);
        }

        using var imagen = SKImage.FromBitmap(bitmap);
        using var datos = imagen.Encode(formato, 95);
        return datos.ToArray();
    }

    /// <summary>Mete un APP1 EXIF (con la orientación y texto de GPS) justo tras el SOI, como lo deja un móvil.</summary>
    private static byte[] ConExif(byte[] jpeg, int orientacion)
    {
        byte[] tiff =
        [
            0x4D, 0x4D, 0x00, 0x2A, 0, 0, 0, 8,
            0, 1,
            0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, (byte)orientacion, 0, 0,
            0, 0, 0, 0,
        ];
        byte[] carga = [.. "Exif\0\0"u8.ToArray(), .. tiff, .. System.Text.Encoding.ASCII.GetBytes(CanarioGps)];
        var longitud = carga.Length + 2;
        byte[] app1 = [0xFF, 0xE1, (byte)(longitud >> 8), (byte)longitud, .. carga];
        return [.. jpeg.Take(2), .. app1, .. jpeg.Skip(2)];
    }

    private static SKColor Pixel(byte[] jpeg, int x, int y)
    {
        using var bitmap = SKBitmap.Decode(jpeg);
        return bitmap.GetPixel(x, y);
    }

    private static bool EsRojo(SKColor c) => c.Red > 170 && c.Blue < 100;

    private static bool EsAzul(SKColor c) => c.Blue > 170 && c.Red < 100;

    private static bool Contiene(byte[] bytes, string texto) => bytes.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(texto)) >= 0;

    [Fact]
    public void El_fixture_lleva_de_verdad_el_EXIF_con_su_orientacion_y_el_GPS()
    {
        // Instrumento: sin esto, «el resultado no tiene EXIF» podría ser cierto porque el original tampoco lo tenía.
        var foto = ConExif(FotoBicolor(40, 20), 6);

        using var codec = SKCodec.Create(SKData.CreateCopy(foto));
        codec!.EncodedOrigin.Should().Be(SKEncodedOrigin.RightTop);
        Contiene(foto, "Exif").Should().BeTrue();
        Contiene(foto, CanarioGps).Should().BeTrue();
    }

    [Theory]
    // origen EXIF, ancho y alto esperados de la foto de 40 x 20, y dónde debe quedar el rojo (que está a la izquierda en el original)
    [InlineData(1, 40, 20, "izquierda")]
    [InlineData(2, 40, 20, "derecha")]
    [InlineData(3, 40, 20, "derecha")]
    [InlineData(4, 40, 20, "izquierda")]
    [InlineData(5, 20, 40, "arriba")]
    [InlineData(6, 20, 40, "arriba")]
    [InlineData(7, 20, 40, "abajo")]
    [InlineData(8, 20, 40, "abajo")]
    public void La_orientacion_EXIF_se_aplica_a_los_pixeles(int orientacion, int ancho, int alto, string dondeEstaElRojo)
    {
        var r = _procesador.Normalizar(ConExif(FotoBicolor(40, 20), orientacion), 3508);

        r.EsExitoso.Should().BeTrue();
        r.Valor.Ancho.Should().Be(ancho);
        r.Valor.Alto.Should().Be(alto);

        var (rojo, azul) = dondeEstaElRojo switch
        {
            "izquierda" => (Pixel(r.Valor.Jpeg, 5, 10), Pixel(r.Valor.Jpeg, 35, 10)),
            "derecha" => (Pixel(r.Valor.Jpeg, 35, 10), Pixel(r.Valor.Jpeg, 5, 10)),
            "arriba" => (Pixel(r.Valor.Jpeg, 10, 5), Pixel(r.Valor.Jpeg, 10, 35)),
            _ => (Pixel(r.Valor.Jpeg, 10, 35), Pixel(r.Valor.Jpeg, 10, 5)),
        };
        EsRojo(rojo).Should().BeTrue($"con la orientación {orientacion} el rojo queda {dondeEstaElRojo}");
        EsAzul(azul).Should().BeTrue();
    }

    [Fact]
    public void Una_foto_sin_orientacion_EXIF_se_deja_como_esta()
    {
        var r = _procesador.Normalizar(FotoBicolor(40, 20), 3508);

        (r.Valor.Ancho, r.Valor.Alto).Should().Be((40, 20));
        EsRojo(Pixel(r.Valor.Jpeg, 5, 10)).Should().BeTrue();
    }

    [Fact]
    public void La_foto_normalizada_no_lleva_EXIF_ni_GPS_ni_ningun_segmento_APP1()
    {
        var original = ConExif(FotoBicolor(40, 20), 6);

        var r = _procesador.Normalizar(original, 3508);

        Contiene(r.Valor.Jpeg, "Exif").Should().BeFalse();
        Contiene(r.Valor.Jpeg, "GPS").Should().BeFalse();
        Contiene(r.Valor.Jpeg, "TalvegCam").Should().BeFalse();
        r.Valor.Jpeg.AsSpan().IndexOf((ReadOnlySpan<byte>)[0xFF, 0xE1]).Should().BeLessThan(0, "ni un solo APP1");
    }

    [Fact]
    public void El_PDF_no_conserva_los_metadatos_de_las_fotos()
    {
        var original = ConExif(FotoBicolor(40, 20), 6);

        // Control: PDFsharp incrusta el JPEG tal cual, así que meter el original dejaría el GPS en el PDF. Si este
        // control dejara de encontrarlo, la prueba de debajo habría dejado de ser sensible.
        Contiene(PdfDirectoDelOriginal(original), CanarioGps).Should().BeTrue("el instrumento puede ver el GPS en un PDF que lo lleve");

        var r = _procesador.ComponerPdf([original, original], CancellationToken.None);

        r.EsExitoso.Should().BeTrue();
        Contiene(r.Valor, CanarioGps).Should().BeFalse();
        Contiene(r.Valor, "TalvegCam").Should().BeFalse();
        Contiene(r.Valor, "Exif").Should().BeFalse();
        Contiene(r.Valor, "GPS").Should().BeFalse();
        Contiene(r.Valor, "/Image").Should().BeTrue("el PDF sí lleva las imágenes");
    }

    private static byte[] PdfDirectoDelOriginal(byte[] jpeg)
    {
        using var documento = new PdfDocument();
        var pagina = documento.AddPage();
        using (var graficos = XGraphics.FromPdfPage(pagina))
        using (var flujo = new MemoryStream(jpeg))
        using (var imagen = XImage.FromStream(flujo))
            graficos.DrawImage(imagen, 10, 10, 100, 50);

        using var salida = new MemoryStream();
        documento.Save(salida, closeStream: false);
        return salida.ToArray();
    }

    [Fact]
    public void El_PDF_tiene_una_pagina_por_foto_en_el_orden_recibido_y_apaisada_o_derecha_segun_la_foto()
    {
        var ancha = FotoBicolor(40, 20);
        var alta = FotoBicolor(20, 40);

        var r = _procesador.ComponerPdf([alta, ancha, alta], CancellationToken.None);

        r.EsExitoso.Should().BeTrue();
        using var leido = PdfReader.Open(new MemoryStream(r.Valor), PdfDocumentOpenMode.Import);
        leido.PageCount.Should().Be(3);
        leido.Pages[0].Width.Point.Should().BeLessThan(leido.Pages[0].Height.Point);
        leido.Pages[1].Width.Point.Should().BeGreaterThan(leido.Pages[1].Height.Point);
        leido.Pages[2].Width.Point.Should().BeLessThan(leido.Pages[2].Height.Point);
    }

    [Fact]
    public void Una_foto_con_orientacion_EXIF_de_cuarto_de_vuelta_sale_derecha_en_su_pagina()
    {
        // Apaisada en bruto (40 x 20) pero con orientación 6: el móvil estaba en vertical. La página tiene que ser derecha.
        var r = _procesador.ComponerPdf([ConExif(FotoBicolor(40, 20), 6)], CancellationToken.None);

        using var leido = PdfReader.Open(new MemoryStream(r.Valor), PdfDocumentOpenMode.Import);
        leido.Pages[0].Width.Point.Should().BeLessThan(leido.Pages[0].Height.Point);
    }

    [Fact]
    public void Una_foto_grande_se_reduce_al_lado_maximo_y_una_pequena_no_se_amplia()
    {
        var grande = _procesador.Normalizar(FotoBicolor(4000, 3000), 1000);
        var pequena = _procesador.Normalizar(FotoBicolor(400, 300), 1000);

        (grande.Valor.Ancho, grande.Valor.Alto).Should().Be((1000, 750));
        (pequena.Valor.Ancho, pequena.Valor.Alto).Should().Be((400, 300));
    }

    [Fact]
    public void Un_PNG_con_transparencia_se_aplana_sobre_blanco()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(10, 10, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.Transparent);
        using var imagen = SKImage.FromBitmap(bitmap);
        using var png = imagen.Encode(SKEncodedImageFormat.Png, 100);

        var r = _procesador.Normalizar(png.ToArray(), 3508);

        r.EsExitoso.Should().BeTrue();
        var pixel = Pixel(r.Valor.Jpeg, 5, 5);
        (pixel.Red, pixel.Green, pixel.Blue).Should().Be(((byte)255, (byte)255, (byte)255));
    }

    [Fact]
    public void El_procesador_vuelve_a_comprobar_las_dimensiones_con_las_que_lee_el_decodificador_antes_de_decodificar()
    {
        // Un PNG de verdad de 12001 x 1: cabe en bytes y en píxeles, pero supera el lado máximo.
        var tira = FotoBicolor(LimitesFotoAPdf.MaximoLadoPixeles + 1, 1, SKEncodedImageFormat.Png);

        var r = _procesador.Normalizar(tira, 3508);

        r.EsFallido.Should().BeTrue();
        r.Error.Codigo.Should().Be(ErroresFotoAPdf.DemasiadosPixeles);
    }

    [Fact]
    public void Lo_que_lleva_firma_de_JPEG_pero_no_se_decodifica_falla_sin_lanzar_excepcion()
    {
        byte[] roto = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, .. new byte[200]];

        var r = _procesador.Normalizar(roto, 3508);

        r.EsFallido.Should().BeTrue();
        r.Error.Codigo.Should().BeOneOf(ErroresFotoAPdf.FotoIlegible, ErroresFotoAPdf.FormatoNoSoportado);
    }

    [Fact]
    public void Un_formato_que_Skia_sabe_leer_pero_no_esta_declarado_se_rechaza_aunque_llegara_al_procesador()
    {
        using var bitmap = new SKBitmap(8, 8);
        bitmap.Erase(SKColors.Red);
        using var imagen = SKImage.FromBitmap(bitmap);
        using var webp = imagen.Encode(SKEncodedImageFormat.Webp, 80);

        var r = _procesador.Normalizar(webp.ToArray(), 3508);

        r.Error.Codigo.Should().Be(ErroresFotoAPdf.FormatoNoSoportado);
    }

    [Fact]
    public void Sin_fotos_el_procesador_no_genera_un_PDF_vacio()
    {
        _procesador.ComponerPdf([], CancellationToken.None).Error.Codigo.Should().Be(ErroresFotoAPdf.SinFotos);
    }

    [Fact]
    public void La_miniatura_sale_girada_y_dentro_del_lado_pedido()
    {
        var r = _procesador.CrearMiniatura(ConExif(FotoBicolor(400, 200), 6), 100, CancellationToken.None);

        r.EsExitoso.Should().BeTrue();
        using var bitmap = SKBitmap.Decode(r.Valor);
        (bitmap.Width, bitmap.Height).Should().Be((50, 100));
    }
}
