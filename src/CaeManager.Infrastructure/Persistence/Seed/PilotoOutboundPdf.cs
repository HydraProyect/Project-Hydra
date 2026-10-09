using System.Buffers.Binary;
using System.Globalization;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// El PDF que acompaña a cada documento de la siembra del piloto: una página con
/// el tipo de documento, el titular, las fechas y la leyenda
/// <see cref="Leyenda"/>. Existe para que «Ver PDF» y la descarga funcionen sobre
/// los datos sembrados con el mismo almacén que usa la aplicación, no para
/// parecerse a un documento real: no lleva logotipos, sellos ni firmas.
///
/// <para>
/// La fuente «DejaVu Sans» la resuelve el <c>GlobalFontSettings.FontResolver</c>
/// que registra <c>CaeManager.Web</c> al arrancar (misma dependencia implícita
/// que <c>EstampadoFirmaEnCampoPdfService</c>).
/// </para>
///
/// <para>
/// La variante pesada incrusta una imagen de ruido determinista: el ruido no se
/// comprime, así que el tamaño del fichero sale del tamaño de la imagen y no
/// depende del compresor. Sirve para ejercitar la subida y la descarga de
/// ficheros grandes por debajo del tope de la aplicación.
/// </para>
/// </summary>
internal static class PilotoOutboundPdf
{
    public const string Leyenda = "Documento de demostración — sin validez";

    private const string NombreFuente = "DejaVu Sans";
    private const double Margen = 56;

    /// <summary>Lado, en píxeles, de la imagen de ruido de la variante pesada: 1500 × 1500 × 3 bytes ≈ 6,4 MiB.</summary>
    internal const int LadoImagenPesada = 1500;

    internal sealed record Datos(
        string TipoDocumento, string Titular, string Organizacion, DateOnly FechaEmision, DateOnly? FechaVencimiento);

    /// <summary>Escribe el PDF en un flujo nuevo, ya rebobinado. El llamante lo libera.</summary>
    public static MemoryStream Generar(Datos datos, bool pesado)
    {
        using var documento = new PdfDocument();
        documento.Info.Title = datos.TipoDocumento;
        documento.Info.Subject = Leyenda;

        var pagina = documento.AddPage();
        using (var graficos = XGraphics.FromPdfPage(pagina))
        {
            var fuenteTitulo = new XFont(NombreFuente, 16, XFontStyleEx.Bold);
            var fuenteTexto = new XFont(NombreFuente, 11, XFontStyleEx.Regular);
            var fuenteLeyenda = new XFont(NombreFuente, 13, XFontStyleEx.Bold);

            var y = Margen + 10;
            graficos.DrawString(datos.TipoDocumento, fuenteTitulo, XBrushes.Black, new XPoint(Margen, y));
            y += 14;
            graficos.DrawLine(XPens.Gray, Margen, y, pagina.Width.Point - Margen, y);
            y += 30;

            foreach (var linea in new[]
                     {
                         $"Titular: {datos.Titular}",
                         $"Organización: {datos.Organizacion}",
                         $"Fecha de emisión: {Fecha(datos.FechaEmision)}",
                         $"Fecha de vencimiento: {(datos.FechaVencimiento is { } vence ? Fecha(vence) : "no caduca")}"
                     })
            {
                graficos.DrawString(linea, fuenteTexto, XBrushes.Black, new XPoint(Margen, y));
                y += 22;
            }

            y += 24;
            graficos.DrawString(Leyenda, fuenteLeyenda, XBrushes.DarkRed, new XPoint(Margen, y));
            y += 30;

            if (pesado)
            {
                using var imagen = XImage.FromStream(new MemoryStream(RuidoBmp(LadoImagenPesada)));
                graficos.DrawImage(imagen, Margen, y, 240, 240);
            }
        }

        var salida = new MemoryStream();
        documento.Save(salida, closeStream: false);
        salida.Position = 0;
        return salida;
    }

    private static string Fecha(DateOnly fecha) => fecha.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>
    /// Un BMP de 24 bits con ruido de un generador xorshift de semilla fija: los
    /// mismos bytes en cada ejecución, sin <see cref="Random"/> ni reloj.
    /// </summary>
    private static byte[] RuidoBmp(int lado)
    {
        const int cabecera = 54;
        var bytesPorFila = (lado * 3 + 3) / 4 * 4;
        var datos = bytesPorFila * lado;
        var bmp = new byte[cabecera + datos];

        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), cabecera);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), lado);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), lado);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28), 24);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(34), datos);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(38), 2835);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(42), 2835);

        var estado = 0x9E3779B9u;
        for (var i = cabecera; i < bmp.Length; i++)
        {
            estado ^= estado << 13;
            estado ^= estado >> 17;
            estado ^= estado << 5;
            bmp[i] = (byte)estado;
        }

        return bmp;
    }
}
