using System.Globalization;
using System.Text;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// El PDF de prueba de varias páginas que <see cref="ComunicacionesDemoCompletaSeeder"/> adjunta a un correo de la demo, para
/// poder dividirlo en tramos desde el reparto de adjuntos sin salir de la aplicación. Escritura a mano, como
/// <see cref="InboundDemoPdf"/>: PDF 1.4, A4, Helvetica sin incrustar, sin imágenes ni dependencias, y los mismos bytes para
/// la misma entrada (ni reloj ni identificadores aleatorios). Una línea de texto por página; sin datos personales.
/// </summary>
internal static class ComunicacionesDemoPdf
{
    /// <summary>Los bytes de un PDF con una página por cada texto de <paramref name="textosPorPagina"/>.</summary>
    public static byte[] Generar(IReadOnlyList<string> textosPorPagina)
    {
        ArgumentNullException.ThrowIfNull(textosPorPagina);
        if (textosPorPagina.Count == 0 || textosPorPagina.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Hace falta al menos una página y ninguna puede estar vacía.", nameof(textosPorPagina));

        var codificacion = Encoding.Latin1;
        var total = textosPorPagina.Count;
        const int objetoFuente = 3;

        // 1 Catálogo, 2 Páginas, 3 Fuente; la página i es el objeto 4 + 2i y su contenido el 5 + 2i.
        var hijas = string.Join(' ', Enumerable.Range(0, total).Select(i => $"{4 + 2 * i} 0 R"));
        var objetos = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{hijas}] /Count {total} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };

        for (var i = 0; i < total; i++)
        {
            var contenido = $"BT /F1 18 Tf 72 760 Td ({Escapar(textosPorPagina[i])}) Tj ET";
            objetos.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents {5 + 2 * i} 0 R /Resources << /Font << /F1 {objetoFuente} 0 R >> >> >>");
            objetos.Add($"<< /Length {codificacion.GetByteCount(contenido)} >>\nstream\n{contenido}\nendstream");
        }

        using var salida = new MemoryStream();
        void Escribir(string cadena) => salida.Write(codificacion.GetBytes(cadena));

        Escribir("%PDF-1.4\n");
        var desplazamientos = new long[objetos.Count];
        for (var i = 0; i < objetos.Count; i++)
        {
            desplazamientos[i] = salida.Position;
            Escribir($"{i + 1} 0 obj\n{objetos[i]}\nendobj\n");
        }

        var inicioTabla = salida.Position;
        Escribir($"xref\n0 {objetos.Count + 1}\n0000000000 65535 f \n");
        foreach (var desplazamiento in desplazamientos)
            Escribir($"{desplazamiento.ToString("D10", CultureInfo.InvariantCulture)} 00000 n \n");
        Escribir($"trailer\n<< /Size {objetos.Count + 1} /Root 1 0 R >>\nstartxref\n{inicioTabla}\n%%EOF\n");

        return salida.ToArray();
    }

    private static string Escapar(string texto) =>
        texto.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);
}
