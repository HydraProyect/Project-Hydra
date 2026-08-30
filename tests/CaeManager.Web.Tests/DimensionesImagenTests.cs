using System.IO.Compression;
using CaeManager.Application.Common;
using CaeManager.Web.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// El tamaño de un archivo de imagen no acota la memoria que cuesta abrirlo:
/// un PNG de un solo color se comprime muchísimo, así que sus dimensiones son
/// libres. Medido contra el código anterior: un PNG de <b>136 KB</b> de
/// 12000 x 12000 —muy por debajo del tope de 10 MB de la subida— hacía que la
/// conversión reservara <b>789 MB</b>, factor 5800, y se aceptaba sin que nada
/// lo mirase.
///
/// Es un vector distinto del de la bomba .zip: el presupuesto del lote cuenta
/// bytes de fichero, y aquí el daño está en los píxeles declarados.
/// </summary>
public class DimensionesImagenTests
{
    /// <summary>PNG de un solo color del tamaño que se pida. Todo ceros: se comprime muchísimo, que es justo el punto.</summary>
    internal static byte[] CrearPngUniforme(int ancho, int alto)
    {
        static void EscribirBigEndian(Stream destino, int valor) =>
            destino.Write([(byte)(valor >> 24), (byte)(valor >> 16), (byte)(valor >> 8), (byte)valor]);

        static byte[] Trozo(string tipo, byte[] datos)
        {
            using var memoria = new MemoryStream();
            EscribirBigEndian(memoria, datos.Length);
            var tipoBytes = System.Text.Encoding.ASCII.GetBytes(tipo);
            memoria.Write(tipoBytes);
            memoria.Write(datos);
            EscribirBigEndian(memoria, unchecked((int)Crc32([.. tipoBytes, .. datos])));
            return memoria.ToArray();
        }

        using var ihdr = new MemoryStream();
        EscribirBigEndian(ihdr, ancho);
        EscribirBigEndian(ihdr, alto);
        ihdr.WriteByte(8);  // profundidad de bit
        ihdr.WriteByte(0);  // escala de grises
        ihdr.WriteByte(0);  // compresión
        ihdr.WriteByte(0);  // filtro
        ihdr.WriteByte(0);  // sin entrelazado

        using var crudo = new MemoryStream();
        var fila = new byte[ancho + 1];
        for (var y = 0; y < alto; y++) crudo.Write(fila);

        using var comprimido = new MemoryStream();
        using (var deflate = new ZLibStream(comprimido, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(crudo.ToArray());

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        png.Write(Trozo("IHDR", ihdr.ToArray()));
        png.Write(Trozo("IDAT", comprimido.ToArray()));
        png.Write(Trozo("IEND", []));
        return png.ToArray();
    }

    private static uint Crc32(byte[] datos)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in datos)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)(-(crc & 1)));
        }

        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>Cabecera JPEG con un SOF0 que declara las dimensiones dadas. No es decodificable, y no hace falta que lo sea: lo que se prueba es la lectura de la cabecera.</summary>
    private static byte[] CrearCabeceraJpeg(int ancho, int alto)
    {
        using var jpeg = new MemoryStream();
        jpeg.Write([0xFF, 0xD8]);                       // SOI

        // Un segmento APP0 por delante, para que el SOF no esté el primero y
        // el recorrido por longitudes tenga algo que saltarse.
        jpeg.Write([0xFF, 0xE0, 0x00, 0x10]);
        jpeg.Write(new byte[14]);

        jpeg.Write([0xFF, 0xC0, 0x00, 0x11, 0x08]);     // SOF0, longitud 17, precisión 8
        jpeg.Write([(byte)(alto >> 8), (byte)alto]);
        jpeg.Write([(byte)(ancho >> 8), (byte)ancho]);
        jpeg.Write(new byte[6]);
        return jpeg.ToArray();
    }

    [Fact]
    public void Un_png_diminuto_que_declara_144_megapixeles_se_rechaza()
    {
        var bomba = CrearPngUniforme(12000, 12000);

        bomba.Length.Should().BeLessThan(10 * 1024 * 1024,
            "el archivo tiene que pasar el tope de subida para que el caso sea el real");
        DimensionesImagen.EstaDentroDelLimite(bomba).Should().BeFalse();
    }

    [Fact]
    public async Task La_conversion_no_llega_a_decodificar_una_imagen_desproporcionada()
    {
        // El caso completo: antes reservaba 789 MB y devolvía un PDF tan campante.
        var bomba = CrearPngUniforme(12000, 12000);

        var convertir = async () => await ConversorArchivosPdf.UnificarAsync(
            [(bomba, "bomba.png")], new ConversorWordFalso());

        await convertir.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Una_imagen_normal_se_sigue_convirtiendo()
    {
        // Control positivo: sin él, un guardia que rechazara todo también
        // pasaría el caso de arriba.
        var foto = CrearPngUniforme(800, 600);

        var pdf = await ConversorArchivosPdf.UnificarAsync([(foto, "foto.png")], new ConversorWordFalso());

        System.Text.Encoding.ASCII.GetString(pdf, 0, 5).Should().Be("%PDF-");
    }

    [Theory]
    // Un A4 escaneado a 600 ppp y una foto de 48 megapíxeles: lo que de verdad
    // sube la gente no puede quedarse fuera por este límite.
    [InlineData(4960, 7016)]
    [InlineData(8000, 6000)]
    public void Lo_que_aparece_en_la_operativa_real_sigue_pasando(int ancho, int alto) =>
        DimensionesImagen.EstaDentroDelLimite(CabeceraPngCon(ancho, alto)).Should().BeTrue();

    /// <summary>
    /// Parchea las dimensiones del IHDR de un PNG mínimo. Generar los píxeles
    /// de un A4 a 600 ppp de verdad costaría 35 MB por caso, y lo que se prueba
    /// aquí es la lectura de la cabecera. El CRC deja de cuadrar tras el
    /// parcheo, cosa que no afecta: esta clase lee el IHDR, no valida el PNG.
    /// </summary>
    private static byte[] CabeceraPngCon(int ancho, int alto)
    {
        var png = CrearPngUniforme(1, 1);
        BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(ancho)).CopyTo(png, 16);
        BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(alto)).CopyTo(png, 20);
        return png;
    }

    [Fact]
    public void Un_jpeg_que_declara_dimensiones_desproporcionadas_se_rechaza() =>
        DimensionesImagen.EstaDentroDelLimite(CrearCabeceraJpeg(30000, 30000)).Should().BeFalse();

    [Fact]
    public void Un_jpeg_de_tamano_normal_pasa() =>
        DimensionesImagen.EstaDentroDelLimite(CrearCabeceraJpeg(4000, 3000)).Should().BeTrue();

    [Fact]
    public void El_ancho_y_el_alto_se_leen_del_sof_aunque_haya_segmentos_delante() =>
        DimensionesImagen.PixelesDeclarados(CrearCabeceraJpeg(1024, 768)).Should().Be(1024L * 768);

    [Fact]
    public void Un_pdf_no_es_una_imagen_y_no_se_rechaza_aqui()
    {
        // Esta comprobación no opina sobre lo que no entiende: de validar el
        // tipo se encarga ValidadorFirmaArchivo, y duplicar ese criterio aquí
        // crearía dos validadores que se desacompasarían.
        var pdf = "%PDF-1.7\nresto del archivo"u8.ToArray();

        DimensionesImagen.PixelesDeclarados(pdf).Should().BeNull();
        DimensionesImagen.EstaDentroDelLimite(pdf).Should().BeTrue();
    }

    private sealed class ConversorWordFalso : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
