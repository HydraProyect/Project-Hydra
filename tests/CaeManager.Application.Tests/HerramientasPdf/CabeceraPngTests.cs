using System.Buffers.Binary;
using CaeManager.Application.HerramientasPdf;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.HerramientasPdf;

/// <summary>
/// La memoria nativa que Skia reserva para un PNG entrelazado (Adam7) se deduce del IHDR, sin decodificar: aquí se fija el
/// cálculo (cota superior: muestras del tipo de color por 1 o 2 bytes) y qué cabeceras no se presupuestan. La medición
/// contra Skia real (que un entrelazado se decodifique y que su búfer se reserve) está en <c>ProcesadorFotosPdfTests</c>.
/// </summary>
public sealed class CabeceraPngTests
{
    /// <summary>Firma, longitud de IHDR, «IHDR» y sus 13 bytes: lo único que lee el lector.</summary>
    private static byte[] Png(uint ancho, uint alto, byte profundidad, byte tipoDeColor, byte entrelazado)
    {
        var c = new byte[33];
        byte[] firma = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        firma.CopyTo(c, 0);
        BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(8), 13);
        "IHDR"u8.CopyTo(c.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(16), ancho);
        BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(20), alto);
        c[24] = profundidad;
        c[25] = tipoDeColor;
        c[28] = entrelazado;
        return c;
    }

    /// <summary>
    /// Todas las combinaciones de la especificación, con la cota del PEOR caso: IHDR no dice si después viene un tRNS, así
    /// que el gris cuenta como gris con alfa y el RGB como RGBA (png_set_tRNS_to_alpha), y la paleta como RGBA expandido.
    /// </summary>
    [Theory]
    [InlineData(8, 6, 4)] // RGBA de 8 bits
    [InlineData(16, 6, 8)] // RGBA de 16 bits: 8 bytes por píxel
    [InlineData(8, 2, 4)] // RGB: con tRNS libpng lo entrega como RGBA
    [InlineData(16, 2, 8)] // RGB de 16 bits con tRNS: RGBA de 16 bits
    [InlineData(1, 0, 2)] // gris de 1 bit: desempaquetado a un byte y, con tRNS, gris con alfa
    [InlineData(2, 0, 2)]
    [InlineData(4, 0, 2)]
    [InlineData(8, 0, 2)] // gris de 8 bits con tRNS: gris con alfa
    [InlineData(16, 0, 4)] // gris de 16 bits con tRNS: gris con alfa de 16 bits
    [InlineData(8, 4, 2)] // gris con alfa
    [InlineData(16, 4, 4)]
    [InlineData(1, 3, 4)] // paleta de 1 bit: desempaquetada y expandida a RGBA, con o sin tRNS
    [InlineData(2, 3, 4)]
    [InlineData(4, 3, 4)]
    [InlineData(8, 3, 4)]
    public void Un_PNG_entrelazado_pide_un_buffer_con_todas_las_filas_en_el_peor_formato_que_puede_entregar_libpng(
        byte profundidad, byte tipoDeColor, int bytesPorPixel)
    {
        var c = CabeceraPng.Leer(Png(7000, 5000, profundidad, tipoDeColor, entrelazado: 1))!;

        c.Entrelazado.Should().BeTrue();
        c.BytesPorPixelNativos.Should().Be(bytesPorPixel);
        c.BytesDeBufferEntrelazado.Should().Be(7000L * 5000 * bytesPorPixel);
        (c.Ancho, c.Alto).Should().Be((7000, 5000));
    }

    [Fact]
    public void Un_PNG_sin_entrelazar_no_pide_buffer_aunque_sea_RGBA_de_16_bits()
    {
        var c = CabeceraPng.Leer(Png(7000, 5000, 16, 6, entrelazado: 0))!;

        c.Entrelazado.Should().BeFalse();
        c.BytesDeBufferEntrelazado.Should().Be(0);
    }

    [Theory]
    [InlineData(0)] // gris
    [InlineData(2)] // RGB
    [InlineData(3)] // paleta
    public void La_cota_no_depende_de_que_haya_tRNS_porque_IHDR_no_lo_dice(byte tipoDeColor)
    {
        // El lector solo ve IHDR: el mismo IHDR seguido o no de un tRNS da la misma cota, y esa cota ya es la del tRNS.
        var sinTrns = Png(100, 100, 8, tipoDeColor, entrelazado: 1);
        byte[] conTrns = [.. sinTrns, 0, 0, 0, 2, .. "tRNS"u8, 0, 0, 0, 0, 0, 0];

        var cotaSin = CabeceraPng.Leer(sinTrns)!.BytesDeBufferEntrelazado;
        var cotaCon = CabeceraPng.Leer(conTrns)!.BytesDeBufferEntrelazado;

        cotaCon.Should().Be(cotaSin);
        var muestrasConAlfa = tipoDeColor == 0 ? 2 : 4;
        cotaSin.Should().BeGreaterThanOrEqualTo(100L * 100 * muestrasConAlfa, "la fila con el alfa que añade el tRNS cabe en la cota");
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(255, 0)]
    [InlineData(0, 255)]
    public void Un_IHDR_con_metodo_de_compresion_o_de_filtro_distinto_de_0_no_se_presupuesta(byte compresion, byte filtro)
    {
        var c = Png(10, 10, 8, 6, 1);
        c[26] = compresion;
        c[27] = filtro;

        CabeceraPng.Leer(c).Should().BeNull("la especificación solo define el método 0 de compresión y el 0 de filtro");
        CabeceraPng.Leer(Png(10, 10, 8, 6, 1)).Should().NotBeNull("con los dos a 0 la misma cabecera se presupuesta");
    }

    [Fact]
    public void El_peor_caso_de_la_regla_de_topes_son_320_millones_de_bytes()
    {
        // 40 megapíxeles en RGBA de 16 bits entrelazado: lo que el hallazgo de la revisión calculaba como exceso.
        CabeceraPng.Leer(Png(8000, 5000, 16, 6, entrelazado: 1))!.BytesDeBufferEntrelazado.Should().Be(320_000_000);
    }

    [Fact]
    public void Lo_que_no_es_un_IHDR_legible_dentro_de_la_especificacion_no_se_presupuesta()
    {
        CabeceraPng.Leer([]).Should().BeNull();
        CabeceraPng.Leer("%PDF-1.7 no soy un PNG de ninguna manera"u8).Should().BeNull();
        CabeceraPng.Leer(Png(10, 10, 8, 6, 0)[..28]).Should().BeNull("IHDR truncado (le falta el byte del método de entrelazado)");

        CabeceraPng.Leer(Png(0, 10, 8, 6, 0)).Should().BeNull("ancho cero");
        CabeceraPng.Leer(Png(10, 0, 8, 6, 0)).Should().BeNull("alto cero");
        CabeceraPng.Leer(Png(10, 10, 8, 6, 2)).Should().BeNull("método de entrelazado inexistente");
        CabeceraPng.Leer(Png(10, 10, 8, 5, 0)).Should().BeNull("tipo de color inexistente");
        CabeceraPng.Leer(Png(10, 10, 16, 3, 0)).Should().BeNull("la paleta no admite 16 bits");
        CabeceraPng.Leer(Png(10, 10, 4, 6, 0)).Should().BeNull("RGBA no admite 4 bits");
        CabeceraPng.Leer(Png(10, 10, 3, 0, 0)).Should().BeNull("profundidad inexistente");

        var sinIhdr = Png(10, 10, 8, 6, 0);
        "IDAT"u8.CopyTo(sinIhdr.AsSpan(12));
        CabeceraPng.Leer(sinIhdr).Should().BeNull("el primer trozo no es IHDR");

        var longitudRara = Png(10, 10, 8, 6, 0);
        BinaryPrimitives.WriteUInt32BigEndian(longitudRara.AsSpan(8), 12);
        CabeceraPng.Leer(longitudRara).Should().BeNull("IHDR mide siempre 13 bytes");
    }
}
