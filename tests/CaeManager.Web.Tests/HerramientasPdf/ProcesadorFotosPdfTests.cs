using CaeManager.Application.Common;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.HerramientasPdf;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

    private readonly SkiaPdfSharpProcesadorFotosPdf _procesador;

    public ProcesadorFotosPdfTests() => _procesador = Procesador(new PresupuestoMemoriaDocumentos(
        Options.Create(new OpcionesPresupuestoMemoriaDocumentos()), TimeProvider.System));

    private static SkiaPdfSharpProcesadorFotosPdf Procesador(IPresupuestoMemoriaDocumentos presupuesto) =>
        new(NullLogger<SkiaPdfSharpProcesadorFotosPdf>.Instance, presupuesto);

    private static PresupuestoMemoriaDocumentos PresupuestoDe(long maximoBytes) =>
        new(Options.Create(new OpcionesPresupuestoMemoriaDocumentos { MaximoBytes = maximoBytes }), TimeProvider.System);

    /// <summary>Deja pasar al presupuesto real y apunta lo máximo que hubo reservado a la vez.</summary>
    private sealed class PresupuestoConPico(PresupuestoMemoriaDocumentos real) : IPresupuestoMemoriaDocumentos
    {
        public long Pico { get; private set; }

        public long Maximo => real.Maximo;

        public ValueTask<ReservaMemoriaDocumentos> ReservarAsync(long bytes, CancellationToken cancellationToken = default) =>
            real.ReservarAsync(bytes, cancellationToken);

        public bool TryReservar(long bytes, out ReservaMemoriaDocumentos reserva)
        {
            var cabe = real.TryReservar(bytes, out reserva);
            Pico = Math.Max(Pico, real.Reservado);
            return cabe;
        }
    }

    /// <summary>Los bytes del PDF compuesto; desecha el resultado como lo haría la pantalla.</summary>
    private static byte[] BytesDelPdf(Result<PdfDeFotos> r)
    {
        r.EsExitoso.Should().BeTrue();
        using var pdf = r.Valor;
        pdf.Flujo.Position.Should().Be(0, "el flujo se entrega listo para leerlo desde el principio");
        return pdf.Flujo.ToArray();
    }

    /// <summary>
    /// Una imagen que se comprime muy bien como PNG (un mosaico de ruido de 64 x 64 repetido: deflate lo encuentra a
    /// 32 KB de distancia) y muy mal como JPEG: su JPEG es órdenes de magnitud mayor que el fichero subido.
    /// </summary>
    private static byte[] PngDeMosaicoDeRuido(int lado)
    {
        var rnd = new Random(7);
        var tesela = new byte[64 * 64 * 4];
        rnd.NextBytes(tesela);
        using var bitmap = new SKBitmap(new SKImageInfo(lado, lado, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var fila = new byte[lado * 4];
        for (var y = 0; y < lado; y++)
        {
            for (var x = 0; x < lado; x++)
            {
                var origen = ((y % 64) * 64 + (x % 64)) * 4;
                fila[x * 4] = tesela[origen];
                fila[x * 4 + 1] = tesela[origen + 1];
                fila[x * 4 + 2] = tesela[origen + 2];
                fila[x * 4 + 3] = 255;
            }

            System.Runtime.InteropServices.Marshal.Copy(fila, 0, bitmap.GetPixels() + y * bitmap.RowBytes, fila.Length);
        }

        using var imagen = SKImage.FromBitmap(bitmap);
        using var datos = imagen.Encode(SKEncodedImageFormat.Png, 100);
        return datos.ToArray();
    }

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

    /// <summary>Cuatro cuadrantes distintos (R arriba izquierda, G arriba derecha, B abajo izquierda, Y abajo derecha).</summary>
    private static byte[] FotoCuadrantes(int ancho, int alto)
    {
        using var bitmap = new SKBitmap(ancho, alto);
        using (var lienzo = new SKCanvas(bitmap))
        {
            void Rellenar(SKColor color, float x, float y)
            {
                using var pintura = new SKPaint { Color = color };
                lienzo.DrawRect(x, y, ancho / 2f, alto / 2f, pintura);
            }

            Rellenar(SKColors.Red, 0, 0);
            Rellenar(SKColors.Lime, ancho / 2f, 0);
            Rellenar(SKColors.Blue, 0, alto / 2f);
            Rellenar(SKColors.Yellow, ancho / 2f, alto / 2f);
        }

        using var imagen = SKImage.FromBitmap(bitmap);
        using var datos = imagen.Encode(SKEncodedImageFormat.Jpeg, 95);
        return datos.ToArray();
    }

    private static char Cuadrante(SKColor c) => c switch
    {
        { Red: > 170, Green: < 100, Blue: < 100 } => 'R',
        { Red: < 100, Green: > 170, Blue: < 100 } => 'G',
        { Red: < 100, Green: < 100, Blue: > 170 } => 'B',
        { Red: > 170, Green: > 170, Blue: < 100 } => 'Y',
        _ => '?',
    };

    [Theory]
    // Esquinas de la imagen derecha (arriba izquierda, arriba derecha, abajo izquierda, abajo derecha) para una foto guardada
    // con R G / B Y. Con cuatro cuadrantes distintos, cada una de las ocho matrices se distingue de las otras siete: el espejo
    // de la 2 no se confunde con el giro de la 3, ni la 5 con la 6, ni la 7 con la 8, ni la 4 con la 1.
    [InlineData(1, "RGBY")]
    [InlineData(2, "GRYB")]
    [InlineData(3, "YBGR")]
    [InlineData(4, "BYRG")]
    [InlineData(5, "RBGY")]
    [InlineData(6, "BRYG")]
    [InlineData(7, "YGBR")]
    [InlineData(8, "GYRB")]
    public void Cada_orientacion_EXIF_coloca_las_cuatro_esquinas_donde_toca(int orientacion, string esquinas)
    {
        var r = _procesador.Normalizar(ConExif(FotoCuadrantes(40, 20), orientacion), 3508);

        r.EsExitoso.Should().BeTrue();
        var (a, h) = (r.Valor.Ancho, r.Valor.Alto);
        var obtenidas = new string(
        [
            Cuadrante(Pixel(r.Valor.Jpeg, a / 4, h / 4)),
            Cuadrante(Pixel(r.Valor.Jpeg, 3 * a / 4, h / 4)),
            Cuadrante(Pixel(r.Valor.Jpeg, a / 4, 3 * h / 4)),
            Cuadrante(Pixel(r.Valor.Jpeg, 3 * a / 4, 3 * h / 4)),
        ]);

        obtenidas.Should().Be(esquinas, $"con la orientación {orientacion}");
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

        var pdf = BytesDelPdf(_procesador.ComponerPdf([original, original], CancellationToken.None));

        Contiene(pdf, CanarioGps).Should().BeFalse();
        Contiene(pdf, "TalvegCam").Should().BeFalse();
        Contiene(pdf, "Exif").Should().BeFalse();
        Contiene(pdf, "GPS").Should().BeFalse();
        Contiene(pdf, "/Image").Should().BeTrue("el PDF sí lleva las imágenes");
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

        var pdf = BytesDelPdf(_procesador.ComponerPdf([alta, ancha, alta], CancellationToken.None));

        using var leido = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        leido.PageCount.Should().Be(3);
        leido.Pages[0].Width.Point.Should().BeLessThan(leido.Pages[0].Height.Point);
        leido.Pages[1].Width.Point.Should().BeGreaterThan(leido.Pages[1].Height.Point);
        leido.Pages[2].Width.Point.Should().BeLessThan(leido.Pages[2].Height.Point);
    }

    [Fact]
    public void Una_foto_con_orientacion_EXIF_de_cuarto_de_vuelta_sale_derecha_en_su_pagina()
    {
        // Apaisada en bruto (40 x 20) pero con orientación 6: el móvil estaba en vertical. La página tiene que ser derecha.
        var pdf = BytesDelPdf(_procesador.ComponerPdf([ConExif(FotoBicolor(40, 20), 6)], CancellationToken.None));

        using var leido = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
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

    [Fact]
    public void Una_foto_alta_sale_con_la_orientacion_aplicada_igual_que_una_pequena()
    {
        var r = _procesador.Normalizar(ConExif(FotoCuadrantes(60, 400), 6), 3508);

        r.EsExitoso.Should().BeTrue();
        (r.Valor.Ancho, r.Valor.Alto).Should().Be((400, 60));
        var (a, h) = (r.Valor.Ancho, r.Valor.Alto);
        new string(
        [
            Cuadrante(Pixel(r.Valor.Jpeg, a / 4, h / 4)),
            Cuadrante(Pixel(r.Valor.Jpeg, 3 * a / 4, h / 4)),
            Cuadrante(Pixel(r.Valor.Jpeg, a / 4, 3 * h / 4)),
            Cuadrante(Pixel(r.Valor.Jpeg, 3 * a / 4, 3 * h / 4)),
        ]).Should().Be("BRYG");
    }

    // --- Memoria: se cuenta lo que SALE, no lo que se subió ---

    [Fact]
    public void El_JPEG_recodificado_puede_ser_mucho_mayor_que_la_foto_subida()
    {
        // Instrumento: sin esto, «la reserva cubre el JPEG» podría ser cierto porque el JPEG siempre fuera pequeño.
        var png = PngDeMosaicoDeRuido(1200);

        var r = _procesador.Normalizar(png, 3508);

        r.EsExitoso.Should().BeTrue();
        r.Valor.Jpeg.Length.Should().BeGreaterThan(5 * png.Length, "el PNG se comprime por su patrón y el JPEG no");
    }

    [Fact]
    public void El_presupuesto_cuenta_el_JPEG_que_retiene_el_PDF_y_no_el_tamano_de_la_foto_subida()
    {
        var png = PngDeMosaicoDeRuido(1200);
        var real = PresupuestoDe(512L * 1024 * 1024);
        var presupuesto = new PresupuestoConPico(real);
        var procesador = Procesador(presupuesto);
        var jpeg = procesador.Normalizar(png, 3508).Valor.Jpeg.LongLength;
        real.Reservado.Should().Be(0, "Normalizar devuelve todo lo que reservó");

        // Lo que presupuestaba la pantalla antes: 2 x bytes subidos + 8 x píxeles de la mayor. Con cuatro fotos de este tipo
        // el PDF solo retiene ya más que eso.
        var presupuestoAnterior = 2 * 4L * png.Length + 8L * 1200 * 1200;
        var r = procesador.ComponerPdf([png, png, png, png], CancellationToken.None);

        r.EsExitoso.Should().BeTrue();
        presupuesto.Pico.Should().BeGreaterThan(presupuestoAnterior, "el presupuesto antiguo no cubría lo que ocupa el PDF");
        presupuesto.Pico.Should().BeGreaterThanOrEqualTo(4 * jpeg * (1 + LimitesFotoAPdf.CopiasDelJpegEnElDocumento));
        real.Reservado.Should().BeGreaterThanOrEqualTo(r.Valor.Flujo.Length, "el PDF que se entrega sigue reservado hasta que se deseche");

        r.Valor.Dispose();
        real.Reservado.Should().Be(0);
    }

    [Fact]
    public void Si_lo_que_retiene_el_PDF_no_cabe_en_el_presupuesto_falla_cerrado_y_lo_devuelve_todo()
    {
        var png = PngDeMosaicoDeRuido(1200);
        // Cabe decodificar y dibujar una foto (8 bytes por píxel) y unos MB más; el PDF de cuatro no cabe.
        var real = PresupuestoDe(8L * 1200 * 1200 + 1024 * 1024);
        var procesador = Procesador(real);

        var accion = () => procesador.ComponerPdf([png, png, png, png], CancellationToken.None);

        accion.Should().Throw<PresupuestoMemoriaAgotadoException>();
        real.Reservado.Should().Be(0, "ni las reservas de las fotos hechas ni el flujo a medias se quedan");
    }

    [Fact]
    public void Sin_hueco_para_decodificar_y_dibujar_falla_antes_de_decodificar_nada()
    {
        var png = PngDeMosaicoDeRuido(600);
        var real = PresupuestoDe(4L * 600 * 600); // solo cabe una de las dos (decodificación y lienzo)
        var etapas = new List<string>();
        var procesador = Procesador(real);
        procesador.AlTerminarEtapa = etapas.Add;

        var accion = () => procesador.Normalizar(png, 3508);

        accion.Should().Throw<PresupuestoMemoriaAgotadoException>();
        etapas.Should().NotContain("decodificada", "solo se leyó la cabecera; la decodificación no empezó");
        real.Reservado.Should().Be(0);
    }

    [Fact]
    public void La_miniatura_tambien_reserva_en_el_presupuesto()
    {
        var png = PngDeMosaicoDeRuido(600);
        var real = PresupuestoDe(4L * 600 * 600);

        var accion = () => Procesador(real).CrearMiniatura(png, 128, CancellationToken.None);

        accion.Should().Throw<PresupuestoMemoriaAgotadoException>();
        real.Reservado.Should().Be(0);
    }

    /// <summary>Un JPEG progresivo REAL (Pillow, 64 x 48, 4:4:4, SOF2 con varios barridos): Skia lo decodifica de verdad.</summary>
    private const string JpegProgresivo444Base64 = "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";

    private static byte[] JpegProgresivo() => Convert.FromBase64String(JpegProgresivo444Base64);

    /// <summary>Misma cabecera con otras dimensiones en el SOF: lo que lee un decodificador antes de empezar, sin datos que las sostengan.</summary>
    private static byte[] ConDimensionesDeSof(byte[] jpeg, ushort ancho, ushort alto)
    {
        var copia = jpeg.ToArray();
        var sof = -1;
        for (var i = 0; i + 4 < copia.Length && sof < 0; i++)
            if (copia[i] == 0xFF && copia[i + 1] == 0xC2 && copia[i + 2] == 0x00 && copia[i + 3] == 0x11)
                sof = i;

        sof.Should().BeGreaterThan(0, "el fixture es progresivo y tiene su SOF2");
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(copia.AsSpan(sof + 5), alto);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(copia.AsSpan(sof + 7), ancho);
        return copia;
    }

    /// <summary>Deja pasar al presupuesto real y apunta cada petición de reserva, quepa o no.</summary>
    private sealed class PresupuestoRegistrador(PresupuestoMemoriaDocumentos real) : IPresupuestoMemoriaDocumentos
    {
        public List<long> Pedidos { get; } = [];

        public long Maximo => real.Maximo;

        public ValueTask<ReservaMemoriaDocumentos> ReservarAsync(long bytes, CancellationToken cancellationToken = default) =>
            real.ReservarAsync(bytes, cancellationToken);

        public bool TryReservar(long bytes, out ReservaMemoriaDocumentos reserva)
        {
            Pedidos.Add(bytes);
            return real.TryReservar(bytes, out reserva);
        }
    }

    [Fact]
    public void Un_JPEG_progresivo_reserva_ademas_el_buffer_de_coeficientes_que_guarda_libjpeg()
    {
        var progresivo = JpegProgresivo();
        var cabecera = CabeceraJpeg.Leer(progresivo)!;
        cabecera.Progresivo.Should().BeTrue("instrumento: el fixture es de verdad progresivo");
        cabecera.BytesDeBufferDeCoeficientes.Should().Be(8L * 6 * 3 * 128, "64 x 48 en 4:4:4: 8 x 6 bloques por componente");

        var real = PresupuestoDe(512L * 1024 * 1024);
        var conProgresivo = new PresupuestoRegistrador(real);
        var r = Procesador(conProgresivo).Normalizar(progresivo, 3508);
        r.EsExitoso.Should().BeTrue("Skia decodifica el progresivo de verdad");

        // Contraste: la misma foto como JPEG de una pasada reserva solo los píxeles decodificados.
        var conBase = new PresupuestoRegistrador(real);
        Procesador(conBase).Normalizar(FotoBicolor(64, 48), 3508).EsExitoso.Should().BeTrue();

        const long Pixeles = 64L * 48 * 4;
        conBase.Pedidos.Should().Contain(Pixeles).And.NotContain(Pixeles + 18432);
        conProgresivo.Pedidos.Should().Contain(Pixeles + 18432, "la decodificación del progresivo suma su búfer de coeficientes");
        real.Reservado.Should().Be(0);
    }

    [Fact]
    public void Un_JPEG_progresivo_cuyo_buffer_de_coeficientes_no_cabe_falla_cerrado_antes_de_decodificar()
    {
        // 6000 x 6000 en 4:4:4: 144 MB de píxeles y 216 MB de coeficientes; el presupuesto admite los píxeles y el lienzo
        // (193 MB) pero no los coeficientes. Sin contarlos, la reserva pasaba y se decodificaba.
        var grande = ConDimensionesDeSof(JpegProgresivo(), 6000, 6000);
        var real = PresupuestoDe(300L * 1000 * 1000);
        var registrador = new PresupuestoRegistrador(real);
        var etapas = new List<string>();
        var procesador = Procesador(registrador);
        procesador.AlTerminarEtapa = etapas.Add;

        var accion = () => procesador.Normalizar(grande, 3508);

        accion.Should().Throw<PresupuestoMemoriaAgotadoException>();
        registrador.Pedidos[0].Should().Be(6000L * 6000 * 4 + 6000L * 6000 * 6);
        etapas.Should().NotContain("decodificada");
        real.Reservado.Should().Be(0);
    }

    [Fact]
    public void Cancelar_mientras_PDFsharp_escribe_el_PDF_no_devuelve_exito_y_libera_las_reservas()
    {
        using var cts = new CancellationTokenSource();
        var real = PresupuestoDe(512L * 1024 * 1024);
        var procesador = Procesador(real);
        var vistas = new List<string>();
        procesador.AlTerminarEtapa = vistas.Add;

        // La cancelación se pide desde el propio flujo de salida, cuando PDFsharp escribe en él: es decir, con Save en curso.
        procesador.AlEscribirElPdf = () =>
        {
            vistas.Add("escritura");
            cts.Cancel();
        };

        var accion = () => procesador.ComponerPdf([FotoBicolor(40, 20)], cts.Token);

        accion.Should().Throw<OperationCanceledException>();
        vistas.Should().Contain("escritura", "instrumento: PDFsharp escribió de verdad en el flujo y la cancelación se pidió ahí");
        vistas.IndexOf("escritura").Should().BeLessThan(vistas.IndexOf("guardado"), "la cancelación llegó ANTES de que Save volviera, no después");
        real.Reservado.Should().Be(0, "ni el PDF ni su reserva quedan retenidos por un resultado que nadie va a recoger");
    }

    [Fact]
    public void Sin_pedir_la_cancelacion_PDFsharp_escribe_en_el_flujo_observado_y_el_PDF_se_entrega()
    {
        var procesador = Procesador(PresupuestoDe(512L * 1024 * 1024));
        var escrituras = 0;
        procesador.AlEscribirElPdf = () => escrituras++;

        var r = procesador.ComponerPdf([FotoBicolor(40, 20)], CancellationToken.None);

        escrituras.Should().BeGreaterThan(0, "contraste del instrumento: el flujo avisa de las escrituras de PDFsharp");
        r.EsExitoso.Should().BeTrue();
        r.Valor.Dispose();
    }

    [Fact]
    public void El_muestreo_de_la_reduccion_no_usa_mipmaps()
    {
        // Con SKMipmapMode.Linear Skia construye y cachea una pirámide que ninguna reserva cuenta.
        SkiaPdfSharpProcesadorFotosPdf.MuestreoDeReduccion.Mipmap.Should().Be(SKMipmapMode.None);
        SkiaPdfSharpProcesadorFotosPdf.MuestreoDeReduccion.Filter.Should().Be(SKFilterMode.Linear);
    }

    /// <summary>
    /// Un PNG RGBA entrelazado (Adam7) construido a mano: Skia no sabe codificarlos. Mitad izquierda roja y mitad derecha
    /// azul. <paramref name="profundidad"/> 8 o 16 bits por canal.
    /// </summary>
    private static byte[] PngEntrelazado(int ancho, int alto, int profundidad = 8)
    {
        (int X0, int Y0, int DX, int DY)[] pasadas = [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];
        var bytesPorMuestra = profundidad / 8;
        var crudo = new List<byte>();
        foreach (var (x0, y0, dx, dy) in pasadas)
        {
            var columnas = (ancho - x0 + dx - 1) / dx;
            var filas = (alto - y0 + dy - 1) / dy;
            if (columnas <= 0 || filas <= 0)
                continue;

            for (var f = 0; f < filas; f++)
            {
                crudo.Add(0); // filtro: ninguno
                for (var c = 0; c < columnas; c++)
                {
                    var izquierda = x0 + c * dx < ancho / 2;
                    foreach (var muestra in new byte[] { (byte)(izquierda ? 255 : 0), 0, (byte)(izquierda ? 0 : 255), 255 })
                        for (var b = 0; b < bytesPorMuestra; b++)
                            crudo.Add(muestra);
                }
            }
        }

        using var comprimido = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(comprimido, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(crudo.ToArray());

        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)ancho);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)alto);
        ihdr[8] = (byte)profundidad;
        ihdr[9] = 6; // RGBA
        ihdr[12] = 1; // Adam7

        return
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            .. TrozoPng("IHDR", ihdr),
            .. TrozoPng("IDAT", comprimido.ToArray()),
            .. TrozoPng("IEND", []),
        ];
    }

    private static byte[] TrozoPng(string tipo, byte[] carga)
    {
        var cuerpo = new byte[4 + carga.Length];
        System.Text.Encoding.ASCII.GetBytes(tipo).CopyTo(cuerpo, 0);
        carga.CopyTo(cuerpo, 4);
        var trozo = new byte[12 + carga.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(trozo, (uint)carga.Length);
        cuerpo.CopyTo(trozo, 4);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(trozo.AsSpan(8 + carga.Length), Crc32(cuerpo));
        return trozo;
    }

    [Theory]
    [InlineData(8, 4)]
    [InlineData(16, 8)]
    public void Un_PNG_entrelazado_reserva_ademas_el_buffer_con_todas_las_filas_que_guarda_Skia(int profundidad, int bytesPorPixelNativos)
    {
        var png = PngEntrelazado(64, 48, profundidad);
        CabeceraPng.Leer(png)!.Entrelazado.Should().BeTrue("instrumento: el fixture es de verdad Adam7");
        SKCodec.Create(new MemoryStream(png)).Should().NotBeNull("instrumento: Skia lo lee");

        var real = PresupuestoDe(512L * 1024 * 1024);
        var conEntrelazado = new PresupuestoRegistrador(real);
        var r = Procesador(conEntrelazado).Normalizar(png, 3508);
        r.EsExitoso.Should().BeTrue("Skia decodifica el entrelazado de verdad");

        // Contraste: el mismo tamaño sin entrelazar reserva solo los píxeles decodificados.
        var conBase = new PresupuestoRegistrador(real);
        Procesador(conBase).Normalizar(FotoBicolor(64, 48, SKEncodedImageFormat.Png), 3508).EsExitoso.Should().BeTrue();

        const long Pixeles = 64L * 48;
        conBase.Pedidos.Should().Contain(Pixeles * 4).And.NotContain(Pixeles * (4 + bytesPorPixelNativos));
        conEntrelazado.Pedidos.Should().Contain(Pixeles * (4 + bytesPorPixelNativos), "la decodificación suma el búfer con todas las filas del entrelazado");
        real.Reservado.Should().Be(0);
    }

    [Fact]
    public void Un_PNG_entrelazado_cuyo_buffer_no_cabe_falla_cerrado_antes_de_decodificar()
    {
        // 64 x 48 en RGBA de 16 bits: 12 KB decodificados, 12 KB de lienzo y 24 KB del búfer de filas del entrelazado. El
        // presupuesto admite lo primero y no la suma: sin contar el búfer, la reserva pasaba y se decodificaba.
        var png = PngEntrelazado(64, 48, profundidad: 16);
        var real = PresupuestoDe(40_000);
        var etapas = new List<string>();
        var procesador = Procesador(real);
        procesador.AlTerminarEtapa = etapas.Add;

        var accion = () => procesador.Normalizar(png, 3508);

        accion.Should().Throw<PresupuestoMemoriaAgotadoException>();
        etapas.Should().NotContain("decodificada");
        real.Reservado.Should().Be(0);
    }

    [Fact]
    public void Un_JPEG_con_dos_SOF_no_se_decodifica_y_no_deja_reservas()
    {
        // El segundo SOF (progresivo y enorme) llega DESPUÉS del primero: el lector de cabecera solo cuenta el primero, y esto
        // fija lo que hace el decodificador desplegado ante el segundo: libjpeg lo rechaza («dos marcadores SOF»).
        var jpeg = FotoBicolor(64, 48);
        var sof = Array.IndexOf(jpeg, (byte)0xC0);
        while (sof > 0 && jpeg[sof - 1] != 0xFF)
            sof = Array.IndexOf(jpeg, (byte)0xC0, sof + 1);
        sof--;
        sof.Should().BeGreaterThan(0, "instrumento: el fixture tiene su SOF0");
        var longitud = (jpeg[sof + 2] << 8) | jpeg[sof + 3];
        byte[] segundoSof = [0xFF, 0xC2, 0x00, 0x11, 0x08, 0x2E, 0xE0, 0x2E, 0xE0, 0x03, 0x01, 0x11, 0x00, 0x02, 0x11, 0x00, 0x03, 0x11, 0x00];
        byte[] conDosSof = [.. jpeg[..(sof + 2 + longitud)], .. segundoSof, .. jpeg[(sof + 2 + longitud)..]];
        CabeceraJpeg.Leer(conDosSof)!.Ancho.Should().Be(64, "instrumento: el lector de cabecera sigue contando el primer SOF");

        var real = PresupuestoDe(512L * 1024 * 1024);
        var etapas = new List<string>();
        var procesador = Procesador(real);
        procesador.AlTerminarEtapa = etapas.Add;

        var r = procesador.Normalizar(conDosSof, 3508);

        r.EsFallido.Should().BeTrue();
        r.Error.Codigo.Should().BeOneOf(ErroresFotoAPdf.FotoIlegible, ErroresFotoAPdf.FormatoNoSoportado);
        etapas.Should().NotContain("decodificada");
        real.Reservado.Should().Be(0);
    }

    [Fact]
    public void Sin_cancelar_el_guardado_el_PDF_se_entrega_y_conserva_su_reserva_hasta_desecharlo()
    {
        var real = PresupuestoDe(512L * 1024 * 1024);
        var vistas = new List<string>();
        var procesador = Procesador(real);
        procesador.AlTerminarEtapa = vistas.Add;

        var r = procesador.ComponerPdf([FotoBicolor(40, 20)], CancellationToken.None);

        r.EsExitoso.Should().BeTrue();
        vistas.Should().Contain("guardado");
        real.Reservado.Should().BeGreaterThan(0);
        r.Valor.Dispose();
        real.Reservado.Should().Be(0);
    }
    [Fact]
    public void El_flujo_del_JPEG_reserva_cada_trozo_antes_de_pedirlo_y_se_declara_agotado_sin_lanzar()
    {
        var trozo = SkiaPdfSharpProcesadorFotosPdf.FlujoJpegContado.Trozo;
        var real = PresupuestoDe(2L * trozo); // caben dos trozos
        using var cuenta = new SkiaPdfSharpProcesadorFotosPdf.Cuenta(real);
        using var flujo = new SkiaPdfSharpProcesadorFotosPdf.FlujoJpegContado(cuenta);
        var datos = new byte[trozo + 10];
        new Random(3).NextBytes(datos);

        flujo.Write(datos, 0, datos.Length);

        flujo.Agotado.Should().BeFalse();
        flujo.Longitud.Should().Be(datos.Length);
        real.Reservado.Should().Be(2L * trozo);
        flujo.ToArray().Should().Equal(datos);

        // Un tercer trozo ya no cabe: no se reserva, no se guarda, no se lanza (estamos dentro de una llamada nativa).
        flujo.Write(new byte[2 * trozo], 0, 2 * trozo);
        flujo.Agotado.Should().BeTrue();
        real.Reservado.Should().Be(2L * trozo, "no se reservó de más");
        flujo.Longitud.Should().BeLessThan(datos.Length + 2L * trozo);
    }

    // --- Cancelación ---

    /// <summary>Una foto cuya decodificación pide muchas lecturas: sirve para cancelar a mitad de decodificar.</summary>
    private byte[] FotoGrande(SKEncodedImageFormat formato)
    {
        var png = PngDeMosaicoDeRuido(1200);
        return formato == SKEncodedImageFormat.Png ? png : _procesador.Normalizar(png, 3508).Valor.Jpeg;
    }

    /// <summary>Cuántas lecturas de bytes pide el decodificador para una foto, sin cancelar.</summary>
    private int LecturasDeUnaDecodificacionCompleta(byte[] foto)
    {
        var lecturas = 0;
        var procesador = Procesador(PresupuestoDe(512L * 1024 * 1024));
        procesador.AlTerminarEtapa = etapa => lecturas += etapa == "lectura" ? 1 : 0;
        procesador.Normalizar(foto, 3508).EsExitoso.Should().BeTrue();
        return lecturas;
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Jpeg)]
    [InlineData(SKEncodedImageFormat.Png)]
    public void Cancelar_interrumpe_la_decodificacion_en_curso_a_los_pocos_KB(SKEncodedImageFormat formato)
    {
        var foto = FotoGrande(formato);
        var total = LecturasDeUnaDecodificacionCompleta(foto);
        total.Should().BeGreaterThan(20, "instrumento: la decodificación completa pide bastantes lecturas, así que cortar a las 5 es cortar a mitad");

        using var cts = new CancellationTokenSource();
        var real = PresupuestoDe(512L * 1024 * 1024);
        var procesador = Procesador(real);
        var etapas = new List<string>();
        var lecturas = 0;
        procesador.AlTerminarEtapa = etapa =>
        {
            etapas.Add(etapa);
            if (etapa == "lectura" && ++lecturas == 5)
                cts.Cancel();
        };

        var accion = () => procesador.Normalizar(foto, 3508, cts.Token);

        accion.Should().Throw<OperationCanceledException>();
        lecturas.Should().Be(5, "tras cancelar, el flujo no sirve ni un byte más");
        etapas.Should().NotContain("decodificada", "la foto no llegó a decodificarse entera");
        real.Reservado.Should().Be(0, "la reserva de la foto cancelada vuelve al presupuesto");
    }

    [Theory]
    [InlineData("decodificada", "dibujada")]
    [InlineData("dibujada", "codificada")]
    [InlineData("codificada", "")]
    public void Cancelar_en_una_etapa_impide_empezar_la_siguiente(string cancelaEn, string noDebeVerse)
    {
        using var cts = new CancellationTokenSource();
        var real = PresupuestoDe(512L * 1024 * 1024);
        var procesador = Procesador(real);
        var etapas = new List<string>();
        procesador.AlTerminarEtapa = etapa =>
        {
            etapas.Add(etapa);
            if (etapa == cancelaEn)
                cts.Cancel();
        };

        var accion = () => procesador.Normalizar(FotoBicolor(40, 20), 3508, cts.Token);

        accion.Should().Throw<OperationCanceledException>();
        etapas.Should().Contain(cancelaEn);
        if (noDebeVerse.Length > 0)
            etapas.Should().NotContain(noDebeVerse);
        real.Reservado.Should().Be(0);
    }

    [Fact]
    public void Cancelar_componiendo_el_PDF_interrumpe_la_foto_en_curso_y_no_deja_reservas()
    {
        using var cts = new CancellationTokenSource();
        var real = PresupuestoDe(512L * 1024 * 1024);
        var procesador = Procesador(real);
        var lecturas = 0;
        procesador.AlTerminarEtapa = etapa =>
        {
            if (etapa == "lectura" && ++lecturas == 5)
                cts.Cancel();
        };

        var accion = () => procesador.ComponerPdf([FotoGrande(SKEncodedImageFormat.Jpeg), FotoBicolor(40, 20)], cts.Token);

        accion.Should().Throw<OperationCanceledException>();
        lecturas.Should().Be(5, "la primera foto no llegó a terminar de decodificarse y la segunda ni se abrió");
        real.Reservado.Should().Be(0);
    }

    [Fact]
    public void Con_el_token_ya_cancelado_no_se_decodifica_nada()
    {
        var etapas = new List<string>();
        var procesador = Procesador(PresupuestoDe(512L * 1024 * 1024));
        procesador.AlTerminarEtapa = etapas.Add;

        var accion = () => procesador.CrearMiniatura(FotoBicolor(40, 20), 128, new CancellationToken(canceled: true));

        accion.Should().Throw<OperationCanceledException>();
        etapas.Should().BeEmpty();
    }

    // --- Metadatos: más allá del canario ASCII en un EXIF ---

    private static byte[] ConSegmentoJpeg(byte[] jpeg, byte marcador, byte[] carga)
    {
        var longitud = carga.Length + 2;
        byte[] segmento = [0xFF, marcador, (byte)(longitud >> 8), (byte)longitud, .. carga];
        return [.. jpeg.Take(2), .. segmento, .. jpeg.Skip(2)];
    }

    /// <summary>EXIF con un IFD de GPS de verdad (GPSLatitudeRef y GPSLatitude como tres racionales), no solo texto.</summary>
    private static byte[] CargaExifConGpsEstructurado()
    {
        byte[] tiff =
        [
            0x4D, 0x4D, 0x00, 0x2A, 0, 0, 0, 8,
            // IFD0: una entrada, GPSInfo (0x8825, LONG) apuntando al IFD de GPS en el desplazamiento 26
            0, 1, 0x88, 0x25, 0, 4, 0, 0, 0, 1, 0, 0, 0, 26,
            0, 0, 0, 0,
            // IFD de GPS: GPSLatitudeRef = "N" y GPSLatitude = 3 racionales en el desplazamiento 56
            0, 2,
            0, 1, 0, 2, 0, 0, 0, 2, (byte)'N', 0, 0, 0,
            0, 2, 0, 5, 0, 0, 0, 3, 0, 0, 0, 56,
            0, 0, 0, 0,
            0, 0, 0, 41, 0, 0, 0, 1, 0, 0, 0, 23, 0, 0, 0, 1, 0, 0, 0, 6, 0, 0, 0, 1,
        ];
        return [.. "Exif\0\0"u8.ToArray(), .. tiff];
    }

    /// <summary>Marcadores de la cabecera del JPEG (hasta SOS), como valores del segundo byte.</summary>
    private static List<byte> MarcadoresDeCabecera(byte[] jpeg)
    {
        var marcadores = new List<byte>();
        var i = 2;
        while (i + 4 <= jpeg.Length && jpeg[i] == 0xFF)
        {
            var m = jpeg[i + 1];
            marcadores.Add(m);
            if (m == 0xDA)
                break;
            i += 2 + ((jpeg[i + 2] << 8) | jpeg[i + 3]);
        }

        return marcadores;
    }

    [Fact]
    public void Ni_el_GPS_estructurado_ni_el_XMP_ni_el_perfil_ICC_ni_un_comentario_llegan_a_la_foto_normalizada()
    {
        var original = FotoBicolor(40, 20);
        original = ConSegmentoJpeg(original, 0xE1, CargaExifConGpsEstructurado());
        original = ConSegmentoJpeg(original, 0xE1, [.. "http://ns.adobe.com/xap/1.0/\0"u8.ToArray(), .. "<x:xmpmeta><exif:GPSLatitude>41,23.1N</exif:GPSLatitude><xmp:CreatorTool>TalvegCam</xmp:CreatorTool></x:xmpmeta>"u8.ToArray()]);
        original = ConSegmentoJpeg(original, 0xE2, [.. "ICC_PROFILE\0\u0001\u0001"u8.ToArray(), .. "PerfilTalvegCam"u8.ToArray()]);
        original = ConSegmentoJpeg(original, 0xFE, "ComentarioTalvegCam"u8.ToArray());

        // Instrumento: el original lleva de verdad los cuatro tipos de segmento.
        MarcadoresDeCabecera(original).Should().Contain([(byte)0xE1, (byte)0xE2, (byte)0xFE]);
        Contiene(original, "xmpmeta").Should().BeTrue();

        var r = _procesador.Normalizar(original, 3508);

        r.EsExitoso.Should().BeTrue();
        MarcadoresDeCabecera(r.Valor.Jpeg).Should().NotContain(m => (m >= 0xE1 && m <= 0xEF) || m == 0xFE, "solo quedan JFIF, tablas y la imagen");
        foreach (var rastro in new[] { "Exif", "xmpmeta", "GPSLatitude", "ICC_PROFILE", "TalvegCam", "Comentario" })
            Contiene(r.Valor.Jpeg, rastro).Should().BeFalse(rastro);
    }

    private static uint Crc32(ReadOnlySpan<byte> datos)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in datos)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }

        return ~crc;
    }

    /// <summary>Un trozo PNG con su CRC, insertado justo tras IHDR.</summary>
    private static byte[] ConTrozoPng(byte[] png, string tipo, byte[] carga)
    {
        var cuerpo = new byte[4 + carga.Length];
        System.Text.Encoding.ASCII.GetBytes(tipo).CopyTo(cuerpo, 0);
        carga.CopyTo(cuerpo, 4);
        var trozo = new byte[12 + carga.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(trozo, (uint)carga.Length);
        cuerpo.CopyTo(trozo, 4);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(trozo.AsSpan(8 + carga.Length), Crc32(cuerpo));
        const int finDeIhdr = 8 + 25;
        return [.. png.Take(finDeIhdr), .. trozo, .. png.Skip(finDeIhdr)];
    }

    [Fact]
    public void Los_metadatos_de_un_PNG_tEXt_zTXt_iTXt_y_eXIf_no_llegan_a_la_foto_normalizada()
    {
        var original = FotoBicolor(40, 20, SKEncodedImageFormat.Png);
        original = ConTrozoPng(original, "tEXt", [.. "Comment\0"u8.ToArray(), .. "GPSLatitude=41.3851N TalvegCam"u8.ToArray()]);
        original = ConTrozoPng(original, "zTXt", CargaZtxt("Description", "GPSLongitude=2.1734E TalvegZtxt"));
        original = ConTrozoPng(original, "iTXt", [.. "XML:com.adobe.xmp\0\0\0\0\0"u8.ToArray(), .. "<x:xmpmeta>TalvegCam</x:xmpmeta>"u8.ToArray()]);
        original = ConTrozoPng(original, "eXIf", CargaExifConGpsEstructurado()[6..]);

        // Instrumento: Skia lee ese PNG sin problema y el original lleva los rastros. El texto de zTXt va comprimido: no
        // se ve en los bytes, así que se comprueba que el trozo existe y que descomprimido lleva el canario.
        Contiene(original, "TalvegCam").Should().BeTrue();
        Contiene(original, "eXIf").Should().BeTrue();
        Contiene(original, "zTXt").Should().BeTrue();
        Contiene(original, "TalvegZtxt").Should().BeFalse("va comprimido con zlib");
        System.Text.Encoding.ASCII.GetString(DescomprimirZlib(CargaZtxt("Description", "GPSLongitude=2.1734E TalvegZtxt")[13..]))
            .Should().Contain("TalvegZtxt");
        SKBitmap.Decode(original).Should().NotBeNull("los trozos añadidos están bien formados");

        var r = _procesador.Normalizar(original, 3508);

        r.EsExitoso.Should().BeTrue();
        MarcadoresDeCabecera(r.Valor.Jpeg).Should().NotContain(m => (m >= 0xE1 && m <= 0xEF) || m == 0xFE);
        foreach (var rastro in new[] { "Exif", "xmpmeta", "GPSLatitude", "TalvegCam", "Comment", "zTXt", "TalvegZtxt", "Description" })
            Contiene(r.Valor.Jpeg, rastro).Should().BeFalse(rastro);
    }

    /// <summary>Carga de un trozo zTXt: palabra clave, byte nulo, método de compresión 0 y el texto comprimido con zlib.</summary>
    private static byte[] CargaZtxt(string palabraClave, string texto)
    {
        using var comprimido = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(comprimido, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(System.Text.Encoding.ASCII.GetBytes(texto));
        return [.. System.Text.Encoding.ASCII.GetBytes(palabraClave), 0, 0, .. comprimido.ToArray()];
    }

    private static byte[] DescomprimirZlib(byte[] datos)
    {
        using var origen = new MemoryStream(datos);
        using var zlib = new System.IO.Compression.ZLibStream(origen, System.IO.Compression.CompressionMode.Decompress);
        using var salida = new MemoryStream();
        zlib.CopyTo(salida);
        return salida.ToArray();
    }
}
