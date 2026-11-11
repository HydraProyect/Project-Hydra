using CaeManager.Application.HerramientasPdf;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.HerramientasPdf;

/// <summary>
/// La memoria nativa que libjpeg reserva para un JPEG progresivo o de varios barridos se deduce de la cabecera, sin
/// decodificar: aquí se fija el cálculo (el de <c>jinit_d_coef_controller</c>: bloques de 8 x 8 x 64 coeficientes de 2
/// bytes por componente, redondeados a la MCU) y la decisión de cuándo hay búfer. La medición contra Skia real está en
/// <c>ProcesadorFotosPdfTests</c> y en la PR.
/// </summary>
public sealed class CabeceraJpegTests
{
    private static readonly (int H, int V)[] Cuatro44 = [(1, 1), (1, 1), (1, 1)];
    private static readonly (int H, int V)[] Cuatro420 = [(2, 2), (1, 1), (1, 1)];

    /// <summary>
    /// Cabeceras de un JPEG hasta el primer SOS: SOI, un APP0, un DHT vacío, el SOF y el SOS con
    /// <paramref name="componentesDelBarrido"/> componentes. No hay datos comprimidos: el lector solo mira cabeceras.
    /// </summary>
    private static byte[] Jpeg(byte sof, ushort ancho, ushort alto, (int H, int V)[] muestreo, int? componentesDelBarrido = null,
        bool conSos = true, byte[]? antesDelSof = null)
    {
        var b = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00 };
        b.AddRange(antesDelSof ?? []);
        b.AddRange([0xFF, 0xC4, 0x00, 0x04, 0x00, 0x00]);
        b.AddRange([0xFF, sof, 0x00, (byte)(8 + 3 * muestreo.Length), 0x08, (byte)(alto >> 8), (byte)alto, (byte)(ancho >> 8), (byte)ancho, (byte)muestreo.Length]);
        for (var i = 0; i < muestreo.Length; i++)
            b.AddRange([(byte)(i + 1), (byte)((muestreo[i].H << 4) | muestreo[i].V), 0x00]);

        if (conSos)
        {
            var ns = componentesDelBarrido ?? muestreo.Length;
            b.AddRange([0xFF, 0xDA, 0x00, (byte)(6 + 2 * ns), (byte)ns]);
            for (var i = 0; i < ns; i++)
                b.AddRange([(byte)(i + 1), 0x00]);
            b.AddRange([0x00, 0x3F, 0x00]);
        }

        b.AddRange([0x12, 0x34, 0xFF, 0xD9]);
        return [.. b];
    }

    [Fact]
    public void Un_JPEG_secuencial_de_un_barrido_se_decodifica_de_una_pasada_y_no_pide_buffer()
    {
        var c = CabeceraJpeg.Leer(Jpeg(0xC0, 4000, 3000, Cuatro44))!;

        c.Progresivo.Should().BeFalse();
        c.VariosBarridos.Should().BeFalse();
        c.NecesitaBufferDeCoeficientes.Should().BeFalse();
        c.BytesDeBufferDeCoeficientes.Should().Be(0);
        (c.Ancho, c.Alto).Should().Be((4000, 3000));
    }

    [Fact]
    public void Un_JPEG_progresivo_4_4_4_cuesta_seis_bytes_por_pixel()
    {
        var c = CabeceraJpeg.Leer(Jpeg(0xC2, 4000, 3000, Cuatro44))!;

        c.Progresivo.Should().BeTrue();
        // 500 x 375 bloques x 3 componentes x 128 bytes por bloque = 6 bytes por píxel de 12 MP.
        c.BytesDeBufferDeCoeficientes.Should().Be(500L * 375 * 3 * 128).And.Be(4000L * 3000 * 6);
    }

    [Fact]
    public void Un_JPEG_progresivo_4_2_0_cuesta_la_mitad_y_redondea_a_la_MCU()
    {
        var c = CabeceraJpeg.Leer(Jpeg(0xC2, 4000, 3000, Cuatro420))!;

        // Luma: 500 x 375 bloques redondeados a múltiplos de 2 = 500 x 376. Cada croma: 250 x 187,5 -> 250 x 188.
        var bloques = 500L * 376 + 2 * (250L * 188);
        c.BytesDeBufferDeCoeficientes.Should().Be(bloques * 128).And.BeLessThan(4000L * 3000 * 3 + 1_000_000);
    }

    [Fact]
    public void Las_dimensiones_que_no_son_multiplo_de_la_MCU_se_redondean_hacia_arriba()
    {
        // 17 x 17 en 4:4:4: 3 x 3 bloques por componente.
        CabeceraJpeg.Leer(Jpeg(0xC2, 17, 17, Cuatro44))!.BytesDeBufferDeCoeficientes.Should().Be(3L * 3 * 3 * 128);

        // 17 x 17 en 4:2:0: la luma, 3 x 3 bloques, sube a la MCU de 2 x 2 (4 x 4); cada croma, 2 x 2.
        CabeceraJpeg.Leer(Jpeg(0xC2, 17, 17, Cuatro420))!.BytesDeBufferDeCoeficientes.Should().Be((16L + 4 + 4) * 128);
    }

    [Theory]
    [InlineData((byte)0xC2)]
    [InlineData((byte)0xC6)]
    [InlineData((byte)0xCA)]
    [InlineData((byte)0xCE)]
    public void Todos_los_SOF_progresivos_piden_buffer_incluidos_los_aritmeticos_y_diferenciales(byte sof)
    {
        var c = CabeceraJpeg.Leer(Jpeg(sof, 1000, 1000, Cuatro44))!;

        c.Progresivo.Should().BeTrue();
        c.BytesDeBufferDeCoeficientes.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData((byte)0xC0)]
    [InlineData((byte)0xC1)]
    [InlineData((byte)0xC9)]
    public void Los_SOF_secuenciales_de_un_barrido_incluido_el_aritmetico_no_piden_buffer(byte sof) =>
        CabeceraJpeg.Leer(Jpeg(sof, 1000, 1000, Cuatro44))!.BytesDeBufferDeCoeficientes.Should().Be(0);

    [Fact]
    public void Un_JPEG_secuencial_cuyo_primer_barrido_no_lleva_todos_los_componentes_tambien_pide_buffer()
    {
        // Baseline con un barrido por componente («no entrelazado»): libjpeg tiene que esperar a los tres para dibujar.
        var c = CabeceraJpeg.Leer(Jpeg(0xC0, 4000, 3000, Cuatro44, componentesDelBarrido: 1))!;

        c.Progresivo.Should().BeFalse();
        c.VariosBarridos.Should().BeTrue();
        c.BytesDeBufferDeCoeficientes.Should().Be(4000L * 3000 * 6);
    }

    [Fact]
    public void Un_JPEG_en_escala_de_grises_progresivo_cuesta_dos_bytes_por_pixel()
    {
        CabeceraJpeg.Leer(Jpeg(0xC2, 800, 640, [(1, 1)]))!.BytesDeBufferDeCoeficientes.Should().Be(800L * 640 * 2);
    }

    [Fact]
    public void Si_no_se_alcanza_el_primer_SOS_se_presupuesta_la_peor_lectura()
    {
        var c = CabeceraJpeg.Leer(Jpeg(0xC0, 4000, 3000, Cuatro44, conSos: false))!;

        c.VariosBarridos.Should().BeTrue("un fichero que no deja ver su primer barrido no se presupuesta como de una pasada");
        c.BytesDeBufferDeCoeficientes.Should().Be(4000L * 3000 * 6);
    }

    [Fact]
    public void Un_SOF_dentro_de_un_segmento_APP_no_se_confunde_con_el_fotograma()
    {
        // Una miniatura progresiva embebida en un APP1: el SOF de verdad (baseline) es el que cuenta.
        byte[] app1 = [0xFF, 0xE1, 0x00, 0x0A, 0xFF, 0xC2, 0x00, 0x0B, 0x08, 0x00, 0x10, 0x00];
        var c = CabeceraJpeg.Leer(Jpeg(0xC0, 4000, 3000, Cuatro44, antesDelSof: app1))!;

        c.Progresivo.Should().BeFalse();
        c.BytesDeBufferDeCoeficientes.Should().Be(0);
    }

    [Fact]
    public void Solo_cuenta_el_primer_SOF()
    {
        var primero = Jpeg(0xC0, 4000, 3000, Cuatro44, conSos: false)[..^4];
        byte[] segundo = [0xFF, 0xC2, 0x00, 0x0B, 0x08, 0x00, 0x10, 0x00, 0x10, 0x01, 0x01, 0x11, 0x00, 0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00];

        var c = CabeceraJpeg.Leer([.. primero, .. segundo])!;

        (c.Ancho, c.Alto).Should().Be((4000, 3000));
        c.Progresivo.Should().BeFalse();
    }

    [Fact]
    public void Lo_que_no_es_un_JPEG_con_SOF_legible_no_se_presupuesta()
    {
        CabeceraJpeg.Leer([]).Should().BeNull();
        CabeceraJpeg.Leer("%PDF-1.7"u8).Should().BeNull();
        CabeceraJpeg.Leer([0xFF, 0xD8, 0xFF, 0xD9]).Should().BeNull("sin SOF");

        // SOF con ancho cero, con un muestreo imposible o con la longitud incoherente con sus componentes.
        CabeceraJpeg.Leer(Jpeg(0xC2, 0, 3000, Cuatro44)).Should().BeNull();
        CabeceraJpeg.Leer(Jpeg(0xC2, 100, 100, [(0, 1), (1, 1), (1, 1)])).Should().BeNull();
        CabeceraJpeg.Leer(Jpeg(0xC2, 100, 100, [(5, 1), (1, 1), (1, 1)])).Should().BeNull();
        var truncado = Jpeg(0xC2, 100, 100, Cuatro44);
        truncado[Array.IndexOf(truncado, (byte)0xC2) + 8] = 4;
        CabeceraJpeg.Leer(truncado).Should().BeNull();
    }
}
