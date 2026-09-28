using System.Buffers.Binary;
using System.Text;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Tenants;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// Conversor del logo del Tenant (contrato del selector de Tenant, § 4.1.2 e invariante I10): solo PNG
/// y JPEG por cabecera, límite de 4096 px leído antes de decodificar, y una salida PNG de 256 × 256
/// reencodada desde el mapa de bits, sin ningún chunk de metadatos del original.
/// </summary>
public class SkiaConversorLogoTenantServiceTests
{
    private const string Marca = "MARCA-QUE-NO-DEBE-SOBREVIVIR";
    private readonly SkiaConversorLogoTenantService _conversor = new();

    [Fact]
    public void Produce_un_png_de_256_por_256()
    {
        var salida = _conversor.ConvertirAPng(Imagen(40, 40, SKEncodedImageFormat.Png));

        using var codec = SKCodec.Create(new SKMemoryStream(salida));
        codec.EncodedFormat.Should().Be(SKEncodedImageFormat.Png);
        codec.Info.Width.Should().Be(256);
        codec.Info.Height.Should().Be(256);
    }

    [Fact]
    public void Encaja_sin_recortar_con_margen_transparente()
    {
        var salida = _conversor.ConvertirAPng(Imagen(100, 50, SKEncodedImageFormat.Png));

        using var bitmap = SKBitmap.Decode(salida);
        bitmap.GetPixel(128, 10).Alpha.Should().Be(0, "la banda superior es margen");
        bitmap.GetPixel(128, 128).Alpha.Should().Be(255, "el centro es la imagen");
        bitmap.GetPixel(2, 128).Alpha.Should().Be(255, "a lo ancho ocupa todo el lado: no se recorta ni se deforma");
    }

    [Fact]
    public void Acepta_JPEG()
    {
        var salida = _conversor.ConvertirAPng(Imagen(30, 30, SKEncodedImageFormat.Jpeg));

        SKCodec.Create(new SKMemoryStream(salida))!.EncodedFormat.Should().Be(SKEncodedImageFormat.Png);
    }

    [Fact]
    public void Rechaza_SVG()
    {
        var svg = Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><script>alert(1)</script></svg>");

        Rechazo(svg).Should().Be(MotivoLogoNoAdmitido.FormatoNoAdmitido);
    }

    [Fact]
    public void Rechaza_WebP_y_GIF_aunque_Skia_sepa_leerlos()
    {
        Rechazo(Imagen(10, 10, SKEncodedImageFormat.Webp)).Should().Be(MotivoLogoNoAdmitido.FormatoNoAdmitido);

        // GIF89a de 1 × 1, mínimo válido.
        byte[] gif =
        [
            0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00,
            0xFF, 0xFF, 0xFF, 0x21, 0xF9, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00, 0x2C, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x01, 0x00, 0x00, 0x02, 0x02, 0x44, 0x01, 0x00, 0x3B
        ];
        Rechazo(gif).Should().Be(MotivoLogoNoAdmitido.FormatoNoAdmitido);
    }

    [Fact]
    public void Rechaza_lo_que_no_es_imagen()
    {
        Rechazo(Encoding.UTF8.GetBytes("%PDF-1.7 no es una imagen")).Should().Be(MotivoLogoNoAdmitido.FormatoNoAdmitido);
    }

    [Fact]
    public void Rechaza_un_PNG_truncado_con_cabecera_valida()
    {
        var completo = Imagen(200, 200, SKEncodedImageFormat.Png);
        var truncado = completo[..(completo.Length / 2)];

        Rechazo(truncado).Should().Be(MotivoLogoNoAdmitido.NoDecodificable);
    }

    [Fact]
    public void Rechaza_una_bomba_de_descompresion_por_la_cabecera()
    {
        // PNG de pocos bytes que declara 20000 × 20000 (1,6 GB en RGBA): la cabecera basta para
        // rechazarlo, antes de reservar el mapa de bits.
        var bomba = PngQueDeclara(20_000, 20_000);
        bomba.Length.Should().BeLessThan(200);

        Rechazo(bomba).Should().Be(MotivoLogoNoAdmitido.DimensionesExcesivas);
    }

    [Theory]
    [InlineData(4_097, 1)]
    [InlineData(1, 4_097)]
    public void Rechaza_un_lado_mayor_de_4096(int ancho, int alto)
    {
        Rechazo(PngQueDeclara(ancho, alto)).Should().Be(MotivoLogoNoAdmitido.DimensionesExcesivas);
    }

    [Fact]
    public void Acepta_exactamente_4096()
    {
        var salida = _conversor.ConvertirAPng(Imagen(4_096, 1, SKEncodedImageFormat.Png));

        SKBitmap.Decode(salida).Width.Should().Be(256);
    }

    [Fact]
    public void Quita_los_chunks_de_texto_EXIF_e_ICC_de_un_PNG()
    {
        var original = InsertarChunksTrasIhdr(
            Imagen(20, 20, SKEncodedImageFormat.Png),
            ("tEXt", Encoding.ASCII.GetBytes("Comment\0" + Marca)),
            ("iTXt", Encoding.ASCII.GetBytes("Autor\0\0\0\0\0" + Marca)),
            ("eXIf", Encoding.ASCII.GetBytes("MM\0*" + Marca)),
            ("iCCP", Encoding.ASCII.GetBytes("perfil\0\0" + Marca)));
        ChunksDe(original).Should().Contain(["tEXt", "iTXt", "eXIf"], "control: el original sí los lleva");

        var salida = _conversor.ConvertirAPng(original);

        ChunksDe(salida).Should().OnlyContain(c => ChunksDelCodificador.Contains(c));
        Encoding.ASCII.GetString(salida).Should().NotContain(Marca);
    }

    [Fact]
    public void Quita_el_EXIF_de_un_JPEG()
    {
        var jpeg = Imagen(20, 20, SKEncodedImageFormat.Jpeg);
        var exif = Encoding.ASCII.GetBytes("Exif\0\0MM\0*" + Marca);
        var app1 = new byte[4 + exif.Length];
        app1[0] = 0xFF;
        app1[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(app1.AsSpan(2), (ushort)(exif.Length + 2));
        exif.CopyTo(app1, 4);
        var conExif = jpeg[..2].Concat(app1).Concat(jpeg[2..]).ToArray();
        Encoding.ASCII.GetString(conExif).Should().Contain(Marca, "control: el original lleva la marca");

        var salida = _conversor.ConvertirAPng(conExif);

        Encoding.ASCII.GetString(salida).Should().NotContain(Marca);
        ChunksDe(salida).Should().OnlyContain(c => ChunksDelCodificador.Contains(c));
    }

    [Fact]
    public void Un_poliglota_no_arrastra_lo_que_va_tras_IEND()
    {
        var poliglota = Imagen(20, 20, SKEncodedImageFormat.Png)
            .Concat(Encoding.ASCII.GetBytes("<html><script>" + Marca + "</script></html>"))
            .ToArray();

        var salida = _conversor.ConvertirAPng(poliglota);

        Encoding.ASCII.GetString(salida).Should().NotContain(Marca);
    }

    // ── Andamiaje ──────────────────────────────────────────────────────────

    private MotivoLogoNoAdmitido? Rechazo(byte[] entrada)
    {
        try
        {
            _conversor.ConvertirAPng(entrada);
            return null;
        }
        catch (LogoTenantNoAdmitidoException ex)
        {
            return ex.Motivo;
        }
    }

    private static byte[] Imagen(int ancho, int alto, SKEncodedImageFormat formato)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(ancho, alto, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(new SKColor(0x1F, 0x6F, 0x4A));
        using var imagen = SKImage.FromBitmap(bitmap);
        using var datos = imagen.Encode(formato, 90);
        return datos.ToArray();
    }

    /// <summary>Firma PNG + IHDR con las dimensiones pedidas + un IDAT mínimo + IEND.</summary>
    private static byte[] PngQueDeclara(int ancho, int alto)
    {
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), ancho);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), alto);
        ihdr[8] = 8;  // profundidad
        ihdr[9] = 6;  // RGBA
        byte[] idat = [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01];
        return Firma.Concat(Chunk("IHDR", ihdr)).Concat(Chunk("IDAT", idat)).Concat(Chunk("IEND", [])).ToArray();
    }

    /// <summary>
    /// Lo único que escribe el codificador PNG de Skia: estructura más <c>sBIT</c> (profundidad de
    /// bits significativos, derivada del mapa de bits de salida, sin contenido del original).
    /// </summary>
    private static readonly HashSet<string> ChunksDelCodificador = ["IHDR", "sBIT", "IDAT", "IEND"];

    private static readonly byte[] Firma = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static byte[] InsertarChunksTrasIhdr(byte[] png, params (string Tipo, byte[] Datos)[] chunks)
    {
        const int finIhdr = 8 + 4 + 4 + 13 + 4;
        var extra = chunks.SelectMany(c => Chunk(c.Tipo, c.Datos)).ToArray();
        return png[..finIhdr].Concat(extra).Concat(png[finIhdr..]).ToArray();
    }

    private static List<string> ChunksDe(byte[] png)
    {
        var tipos = new List<string>();
        var posicion = 8;
        while (posicion + 8 <= png.Length)
        {
            var longitud = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(posicion));
            var tipo = Encoding.ASCII.GetString(png, posicion + 4, 4);
            tipos.Add(tipo);
            posicion += 12 + longitud;
            if (tipo == "IEND") break;
        }
        return tipos;
    }

    private static byte[] Chunk(string tipo, byte[] datos)
    {
        var tipoBytes = Encoding.ASCII.GetBytes(tipo);
        var salida = new byte[12 + datos.Length];
        BinaryPrimitives.WriteInt32BigEndian(salida.AsSpan(0), datos.Length);
        tipoBytes.CopyTo(salida, 4);
        datos.CopyTo(salida, 8);
        BinaryPrimitives.WriteUInt32BigEndian(salida.AsSpan(8 + datos.Length), Crc32(tipoBytes.Concat(datos).ToArray()));
        return salida;
    }

    private static uint Crc32(byte[] datos)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in datos)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}
