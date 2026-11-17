using System.Globalization;
using System.Text;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// El PDF de prueba que <see cref="InboundDemoSeeder"/> adjunta a cada Documento del Tenant TEC de la demo, para que el Gestor
/// CAE del Tenant TCE pueda validarlo en directo (sin archivo, <c>RegistrarEvidenciaInboundCommand</c> devuelve
/// <c>OriginalSinArchivo</c> y nada llega a «Apto»). Una página con una sola línea de texto, sin fuentes incrustadas, sin
/// imágenes y sin ninguna dependencia: la escritura es a mano para que no dependa del <c>FontResolver</c> que registra la
/// aplicación al arrancar (como sí lo hace <c>PilotoOutboundPdf</c>) y salga igual en cualquier arnés.
///
/// <para>
/// No lleva datos personales ni contenido médico: el texto es una constante. La del certificado de aptitud médica es la
/// única conclusión, <see cref="TextoDeAptitud"/>; la Evidencia Inbound de un tipo de solo conclusión no copia el binario.
/// </para>
/// </summary>
internal static class InboundDemoPdf
{
    public const string TextoGeneral = "Documento de prueba · demo";

    public const string TextoDeAptitud = "Apto · documento de prueba";

    /// <summary>El texto de la única página: el de la aptitud médica, o el general.</summary>
    public static string TextoDe(bool soloConclusionAptitud) => soloConclusionAptitud ? TextoDeAptitud : TextoGeneral;

    /// <summary>
    /// Los bytes de un PDF 1.4 de una página A4 con <paramref name="texto"/> en Helvetica (WinAnsi: el punto medio es
    /// 0xB7). Mismos bytes para el mismo texto: ni reloj ni identificadores aleatorios.
    /// </summary>
    public static byte[] Generar(string texto)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(texto);

        var contenido = $"BT /F1 18 Tf 72 760 Td ({Escapar(texto)}) Tj ET";
        var codificacion = Encoding.Latin1;

        string[] objetos =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {codificacion.GetByteCount(contenido)} >>\nstream\n{contenido}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        ];

        using var salida = new MemoryStream();
        void Escribir(string cadena) => salida.Write(codificacion.GetBytes(cadena));

        Escribir("%PDF-1.4\n");
        var desplazamientos = new long[objetos.Length];
        for (var i = 0; i < objetos.Length; i++)
        {
            desplazamientos[i] = salida.Position;
            Escribir($"{i + 1} 0 obj\n{objetos[i]}\nendobj\n");
        }

        var inicioTabla = salida.Position;
        Escribir($"xref\n0 {objetos.Length + 1}\n0000000000 65535 f \n");
        foreach (var desplazamiento in desplazamientos)
            Escribir($"{desplazamiento.ToString("D10", CultureInfo.InvariantCulture)} 00000 n \n");
        Escribir($"trailer\n<< /Size {objetos.Length + 1} /Root 1 0 R >>\nstartxref\n{inicioTabla}\n%%EOF\n");

        return salida.ToArray();
    }

    private static string Escapar(string texto) =>
        texto.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);
}
