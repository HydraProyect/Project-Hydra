using System.Buffers.Binary;
using CaeManager.Application.Common;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Domain.Common;
using FluentAssertions;

namespace CaeManager.Application.Tests.HerramientasPdf;

/// <summary>
/// Los topes de Foto a PDF que se deciden sin decodificar: tamaño, firma de bytes y dimensiones de la cabecera. El
/// orientado, la reducción y la ausencia de metadatos son del procesador de Infrastructure y se prueban con imágenes
/// reales en <c>CaeManager.Web.Tests</c> (Application no depende de SkiaSharp ni de PDFsharp).
/// </summary>
public sealed class FotosAPdfTests
{
    // --- Fixtures: cabeceras mínimas, construidas a mano. El validador no lee más allá del SOF / IHDR. ---

    private static byte[] Png(uint ancho, uint alto)
    {
        var c = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(c, 0);
        BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(8), 13);
        "IHDR"u8.CopyTo(c.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(16), ancho);
        BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(20), alto);
        return c;
    }

    private static byte[] Sof(ushort ancho, ushort alto, byte marcador = 0xC0)
    {
        var s = new byte[2 + 2 + 1 + 2 + 2 + 1 + 3];
        s[0] = 0xFF;
        s[1] = marcador;
        BinaryPrimitives.WriteUInt16BigEndian(s.AsSpan(2), 11);
        s[4] = 8;
        BinaryPrimitives.WriteUInt16BigEndian(s.AsSpan(5), alto);
        BinaryPrimitives.WriteUInt16BigEndian(s.AsSpan(7), ancho);
        s[9] = 1;
        s[10] = 1;
        s[11] = 0x11;
        return s;
    }

    private static byte[] App(byte marcador, byte[] carga)
    {
        var s = new byte[4 + carga.Length];
        s[0] = 0xFF;
        s[1] = marcador;
        BinaryPrimitives.WriteUInt16BigEndian(s.AsSpan(2), (ushort)(carga.Length + 2));
        carga.CopyTo(s, 4);
        return s;
    }

    private static byte[] Jpeg(ushort ancho, ushort alto, params byte[][] antesDelSof) =>
        [.. new byte[] { 0xFF, 0xD8 }, .. antesDelSof.SelectMany(x => x), .. Sof(ancho, alto), 0xFF, 0xDA];

    private static byte[] ConRelleno(byte[] cabecera, int longitud)
    {
        var c = new byte[longitud];
        cabecera.CopyTo(c, 0);
        return c;
    }

    private static string? Codigo(Result<FotoValidada> r) => r.EsFallido ? r.Error.Codigo : null;

    // --- Firma de bytes ---

    [Fact]
    public void Un_JPEG_y_un_PNG_normales_se_aceptan_con_las_dimensiones_de_su_cabecera()
    {
        var jpeg = ValidadorFotoAPdf.Validar(Jpeg(4000, 3000, App(0xE0, [0x4A, 0x46, 0x49, 0x46, 0])));
        var png = ValidadorFotoAPdf.Validar(Png(1080, 1920));

        jpeg.EsExitoso.Should().BeTrue();
        jpeg.Valor.Should().Be(new FotoValidada(FormatoFoto.Jpeg, 4000, 3000));
        png.EsExitoso.Should().BeTrue();
        png.Valor.Should().Be(new FotoValidada(FormatoFoto.Png, 1080, 1920));
    }

    [Theory]
    [InlineData("%PDF-1.7\n1 0 obj")]
    [InlineData("GIF89a\u0001\u0000\u0001\u0000")]
    [InlineData("BM\u0000\u0000\u0000\u0000\u0000\u0000\u0000\u0000\u0000\u0000")]
    [InlineData("RIFF\u0000\u0000\u0000\u0000WEBPVP8 ")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'/>")]
    public void Lo_que_no_es_JPEG_ni_PNG_se_rechaza_por_sus_bytes_aunque_se_llame_foto_jpg(string contenido)
    {
        // El validador solo recibe los bytes: no hay nombre ni tipo MIME con los que dejarse engañar.
        var r = ValidadorFotoAPdf.Validar(System.Text.Encoding.Latin1.GetBytes(contenido));

        Codigo(r).Should().Be(ErroresFotoAPdf.FormatoNoSoportado);
    }

    [Theory]
    [InlineData("heic")]
    [InlineData("heix")]
    [InlineData("mif1")]
    [InlineData("heif")]
    public void Un_HEIC_se_rechaza_con_su_propio_codigo_para_poder_explicar_que_hacer(string marca)
    {
        var heic = new byte[24];
        BinaryPrimitives.WriteUInt32BigEndian(heic, 24);
        "ftyp"u8.CopyTo(heic.AsSpan(4));
        System.Text.Encoding.ASCII.GetBytes(marca).CopyTo(heic, 8);

        Codigo(ValidadorFotoAPdf.Validar(heic)).Should().Be(ErroresFotoAPdf.HeicNoSoportado);
    }

    [Fact]
    public void Un_ftyp_de_otro_contenedor_no_es_HEIC()
    {
        var mp4 = new byte[24];
        "ftyp"u8.CopyTo(mp4.AsSpan(4));
        "isom"u8.CopyTo(mp4.AsSpan(8));

        Codigo(ValidadorFotoAPdf.Validar(mp4)).Should().Be(ErroresFotoAPdf.FormatoNoSoportado);
    }

    [Fact]
    public void Un_JPEG_o_PNG_con_la_firma_mal_no_se_acepta_por_parecerse()
    {
        var jpegSinTercerByte = Jpeg(100, 100);
        jpegSinTercerByte = [0xFF, 0xD8, 0x00, .. jpegSinTercerByte.Skip(3)];
        var pngConFirmaTruncada = Png(100, 100).Skip(1).ToArray();

        Codigo(ValidadorFotoAPdf.Validar(jpegSinTercerByte)).Should().Be(ErroresFotoAPdf.FormatoNoSoportado);
        Codigo(ValidadorFotoAPdf.Validar(pngConFirmaTruncada)).Should().Be(ErroresFotoAPdf.FormatoNoSoportado);
    }

    // --- Tamaño ---

    [Fact]
    public void Una_foto_vacia_se_rechaza()
    {
        Codigo(ValidadorFotoAPdf.Validar([])).Should().Be(ErroresFotoAPdf.FotoVacia);
    }

    [Fact]
    public void El_limite_de_bytes_por_foto_es_inclusivo_y_un_byte_mas_se_rechaza()
    {
        var justo = ConRelleno(Jpeg(100, 100), (int)LimitesFotoAPdf.TamanoMaximoFotoBytes);
        var deMas = ConRelleno(Jpeg(100, 100), (int)LimitesFotoAPdf.TamanoMaximoFotoBytes + 1);

        ValidadorFotoAPdf.Validar(justo).EsExitoso.Should().BeTrue();
        Codigo(ValidadorFotoAPdf.Validar(deMas)).Should().Be(ErroresFotoAPdf.FotoDemasiadoGrande);
    }

    // --- Píxeles declarados ---

    [Theory]
    [InlineData(6000u, 6000u, true)]     // 36 MP: una cámara de 48 MP binada
    [InlineData(12000u, 3333u, true)]    // 39,996 MP: justo bajo el tope
    [InlineData(12000u, 3334u, false)]   // 40,008 MP: justo por encima
    [InlineData(7000u, 7000u, false)]    // 49 MP
    [InlineData(12001u, 1u, false)]      // una tira: cabe en píxeles pero no es una foto
    [InlineData(1u, 12001u, false)]
    [InlineData(20000u, 20000u, false)]
    [InlineData(uint.MaxValue, uint.MaxValue, false)] // el producto desborda un long: no puede salir negativo y pasar
    [InlineData(uint.MaxValue, 1u, false)]
    public void Los_pixeles_de_un_PNG_se_acotan_leyendo_la_cabecera(uint ancho, uint alto, bool admitida)
    {
        var r = ValidadorFotoAPdf.Validar(Png(ancho, alto));

        if (admitida)
            r.EsExitoso.Should().BeTrue();
        else
            Codigo(r).Should().Be(ErroresFotoAPdf.DemasiadosPixeles);
    }

    [Theory]
    [InlineData((ushort)6000, (ushort)6000, true)]
    [InlineData((ushort)12000, (ushort)3333, true)]
    [InlineData((ushort)12000, (ushort)3334, false)]
    [InlineData((ushort)65535, (ushort)65535, false)]
    [InlineData((ushort)65535, (ushort)1, false)]
    public void Los_pixeles_de_un_JPEG_se_acotan_leyendo_el_SOF(ushort ancho, ushort alto, bool admitida)
    {
        var r = ValidadorFotoAPdf.Validar(Jpeg(ancho, alto));

        if (admitida)
            r.EsExitoso.Should().BeTrue();
        else
            Codigo(r).Should().Be(ErroresFotoAPdf.DemasiadosPixeles);
    }

    [Theory]
    [InlineData((byte)0xC0)] // baseline
    [InlineData((byte)0xC1)]
    [InlineData((byte)0xC2)] // progresivo: lo que sacan muchos móviles
    public void Cualquier_SOF_de_cabecera_de_fotograma_cuenta(byte marcador)
    {
        byte[] jpeg = [0xFF, 0xD8, .. Sof(30000, 30000, marcador)];

        Codigo(ValidadorFotoAPdf.Validar(jpeg)).Should().Be(ErroresFotoAPdf.DemasiadosPixeles);
    }

    [Theory]
    [InlineData((byte)0xC4)] // DHT: tablas de Huffman, que van antes del SOF en muchos JPEG
    [InlineData((byte)0xC8)] // JPG reservado
    [InlineData((byte)0xCC)] // DAC
    public void Un_marcador_C4_C8_o_CC_antes_del_SOF_no_se_toma_por_un_SOF(byte marcador)
    {
        // Su carga imita un SOF con 65535 x 65535: si el recorrido lo leyera como cabecera de fotograma, la foto normal
        // (4000 x 3000) se rechazaría por píxeles.
        var impostor = App(marcador, [0x08, 0xFF, 0xFF, 0xFF, 0xFF, 0x01, 0x01, 0x11, 0x00]);

        var r = ValidadorFotoAPdf.Validar(Jpeg(4000, 3000, impostor));

        r.EsExitoso.Should().BeTrue();
        r.Valor.Should().Be(new FotoValidada(FormatoFoto.Jpeg, 4000, 3000));
    }

    [Fact]
    public void El_SOF_de_una_miniatura_dentro_del_EXIF_no_se_toma_por_el_de_la_imagen()
    {
        // La miniatura que lleva el APP1 declara 10 x 10; la imagen real, 30000 x 30000. Si el recorrido entrase en el
        // segmento, daría 100 píxeles y la bomba pasaría el límite.
        byte[] miniatura = [0xFF, 0xD8, .. Sof(10, 10), 0xFF, 0xD9];
        var exif = App(0xE1, [.. "Exif\0\0"u8.ToArray(), .. miniatura]);

        Codigo(ValidadorFotoAPdf.Validar(Jpeg(30000, 30000, exif))).Should().Be(ErroresFotoAPdf.DemasiadosPixeles);
    }

    [Fact]
    public void Una_miniatura_enorme_dentro_del_EXIF_no_hace_rechazar_una_foto_normal()
    {
        byte[] miniatura = [0xFF, 0xD8, .. Sof(30000, 30000), 0xFF, 0xD9];
        var exif = App(0xE1, [.. "Exif\0\0"u8.ToArray(), .. miniatura]);

        ValidadorFotoAPdf.Validar(Jpeg(4000, 3000, exif)).Valor.Should().Be(new FotoValidada(FormatoFoto.Jpeg, 4000, 3000));
    }

    [Fact]
    public void Un_JPEG_sin_SOF_antes_de_los_datos_no_se_acepta()
    {
        byte[] sinSof = [0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x02, 0x01, 0x02, 0x03];
        byte[] truncado = [0xFF, 0xD8, 0xFF, 0xE1, 0xFF, 0xFF, 0x01];
        byte[] altoEnCero = [0xFF, 0xD8, .. Sof(100, 0)];
        byte[] solo3Bytes = [0xFF, 0xD8, 0xFF];

        Codigo(ValidadorFotoAPdf.Validar(sinSof)).Should().Be(ErroresFotoAPdf.FotoIlegible);
        Codigo(ValidadorFotoAPdf.Validar(truncado)).Should().Be(ErroresFotoAPdf.FotoIlegible);
        Codigo(ValidadorFotoAPdf.Validar(altoEnCero)).Should().Be(ErroresFotoAPdf.FotoIlegible);
        Codigo(ValidadorFotoAPdf.Validar(solo3Bytes)).Should().Be(ErroresFotoAPdf.FotoIlegible);
    }

    [Fact]
    public void Un_PNG_sin_IHDR_en_su_sitio_o_truncado_no_se_acepta()
    {
        var sinIhdr = Png(100, 100);
        "IDAT"u8.CopyTo(sinIhdr.AsSpan(12));
        var longitudDistinta = Png(100, 100);
        BinaryPrimitives.WriteUInt32BigEndian(longitudDistinta.AsSpan(8), 14);

        Codigo(ValidadorFotoAPdf.Validar(sinIhdr)).Should().Be(ErroresFotoAPdf.FotoIlegible);
        Codigo(ValidadorFotoAPdf.Validar(longitudDistinta)).Should().Be(ErroresFotoAPdf.FotoIlegible);
        Codigo(ValidadorFotoAPdf.Validar(Png(100, 100).Take(20).ToArray())).Should().Be(ErroresFotoAPdf.FotoIlegible);
        Codigo(ValidadorFotoAPdf.Validar(Png(0, 100))).Should().Be(ErroresFotoAPdf.FotoIlegible);
    }

    // --- El generador ---

    private sealed class Disponibilidad(bool disponible) : IDisponibilidadHerramientasPdf
    {
        public ValueTask<bool> EstaDisponibleAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(disponible);
    }

    private sealed class ProcesadorFalso : IProcesadorFotosPdf
    {
        public List<IReadOnlyList<byte[]>> Llamadas { get; } = [];

        public Result<PdfDeFotos> ComponerPdf(IReadOnlyList<byte[]> fotos, CancellationToken cancellationToken)
        {
            Llamadas.Add(fotos);
            return Result.Exito(new PdfDeFotos(new MemoryStream([1, 2, 3]), new Nada()));
        }

        public Result<byte[]> CrearMiniatura(byte[] foto, int ladoMaximoPixeles, CancellationToken cancellationToken) =>
            Result.Exito<byte[]>([9]);

        private sealed class Nada : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private static Task<Result<PdfDeFotos>> Generar(ProcesadorFalso procesador, IReadOnlyList<byte[]> fotos, bool disponible = true) =>
        new GeneradorPdfDesdeFotos(new Disponibilidad(disponible), procesador)
            .GenerarAsync(fotos, CancellationToken.None);

    [Fact]
    public async Task El_generador_pasa_las_fotos_al_procesador_en_el_orden_recibido()
    {
        var procesador = new ProcesadorFalso();
        var a = Jpeg(100, 200);
        var b = Png(300, 400);
        var c = Jpeg(500, 600);

        var r = await Generar(procesador, [b, c, a]);

        r.EsExitoso.Should().BeTrue();
        r.Valor.Flujo.ToArray().Should().Equal(1, 2, 3);
        procesador.Llamadas.Should().ContainSingle().Which.Should().Equal(b, c, a);
    }

    [Fact]
    public void Desechar_el_PDF_cierra_el_flujo_y_devuelve_su_reserva_de_memoria()
    {
        var presupuesto = new PresupuestoMemoriaDocumentos(
            Microsoft.Extensions.Options.Options.Create(new OpcionesPresupuestoMemoriaDocumentos()), TimeProvider.System);
        presupuesto.TryReservar(5 * 1024 * 1024, out var reserva).Should().BeTrue();
        var flujo = new MemoryStream([1, 2, 3]);
        var pdf = new PdfDeFotos(flujo, reserva);
        presupuesto.Reservado.Should().Be(5 * 1024 * 1024);

        pdf.Dispose();

        presupuesto.Reservado.Should().Be(0, "el PDF y su reserva viajan juntos");
        flujo.CanRead.Should().BeFalse();
    }

    [Fact]
    public async Task El_limite_de_fotos_es_inclusivo_y_una_mas_se_rechaza_sin_procesar_nada()
    {
        var procesador = new ProcesadorFalso();
        var foto = Jpeg(100, 100);

        var justo = await Generar(procesador, Enumerable.Repeat(foto, LimitesFotoAPdf.MaximoFotos).ToList());
        var deMas = await Generar(procesador, Enumerable.Repeat(foto, LimitesFotoAPdf.MaximoFotos + 1).ToList());

        justo.EsExitoso.Should().BeTrue();
        deMas.Error.Codigo.Should().Be(ErroresFotoAPdf.DemasiadasFotos);
        procesador.Llamadas.Should().HaveCount(1, "solo la primera llamada llegó al procesador");
    }

    [Fact]
    public async Task Sin_fotos_no_hay_PDF()
    {
        var procesador = new ProcesadorFalso();

        var r = await Generar(procesador, []);

        r.Error.Codigo.Should().Be(ErroresFotoAPdf.SinFotos);
        procesador.Llamadas.Should().BeEmpty();
    }

    [Fact]
    public async Task El_total_de_bytes_tambien_se_acota_aunque_cada_foto_este_dentro_de_su_limite()
    {
        var procesador = new ProcesadorFalso();
        // 6 x 14 MB = 84 MB: cada una bajo los 15 MB, el total por encima de 80 MB.
        var grande = ConRelleno(Jpeg(100, 100), 14 * 1024 * 1024);

        var r = await Generar(procesador, Enumerable.Repeat(grande, 6).ToList());

        r.Error.Codigo.Should().Be(ErroresFotoAPdf.TotalDemasiadoGrande);
        procesador.Llamadas.Should().BeEmpty();
    }

    [Fact]
    public async Task El_generador_repite_la_validacion_y_dice_que_foto_falla()
    {
        var procesador = new ProcesadorFalso();
        byte[] pdfDisfrazado = System.Text.Encoding.ASCII.GetBytes("%PDF-1.7 esto no es una foto");

        var r = await Generar(procesador, [Jpeg(100, 100), Png(100, 100), pdfDisfrazado]);

        r.Error.Codigo.Should().Be(ErroresFotoAPdf.FormatoNoSoportado);
        r.Error.Mensaje.Should().StartWith("Foto 3:");
        procesador.Llamadas.Should().BeEmpty();
    }

    [Fact]
    public async Task El_generador_rechaza_una_foto_con_demasiados_pixeles_sin_llegar_al_procesador()
    {
        var procesador = new ProcesadorFalso();

        var r = await Generar(procesador, [Png(100, 100), Png(uint.MaxValue, uint.MaxValue)]);

        r.Error.Codigo.Should().Be(ErroresFotoAPdf.DemasiadosPixeles);
        r.Error.Mensaje.Should().StartWith("Foto 2:");
        procesador.Llamadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Si_Herramientas_PDF_no_esta_disponible_no_se_procesa_nada()
    {
        var procesador = new ProcesadorFalso();

        var r = await Generar(procesador, [Jpeg(100, 100)], disponible: false);

        r.Error.Codigo.Should().Be(ErroresFotoAPdf.NoDisponible);
        procesador.Llamadas.Should().BeEmpty();
    }
}
