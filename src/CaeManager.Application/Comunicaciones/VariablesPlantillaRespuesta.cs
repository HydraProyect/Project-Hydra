using System.Text.RegularExpressions;

namespace CaeManager.Application.Comunicaciones;

public static partial class VariablesPlantillaRespuesta
{
    [GeneratedRegex(@"\{\{\s*([^{}]+?)\s*\}\}")]
    private static partial Regex PatronVariable();

    public static IReadOnlyList<string> Pendientes(string texto) =>
        PatronVariable().Matches(texto).Select(m => m.Groups[1].Value).Distinct().ToList();

    // Una sola pasada: los datos de una Empresa nunca se interpretan como otra variable.
    public static string Rellenar(string cuerpoHtml, IReadOnlyDictionary<string, string?> valores) =>
        PatronVariable().Replace(cuerpoHtml, m =>
            valores.TryGetValue(m.Groups[1].Value, out var valor) && !string.IsNullOrWhiteSpace(valor)
                ? EscaparHtml(valor)
                : m.Value);

    // Texto que no es de confianza (lo redacta un modelo a partir de un correo ajeno): nada de él se interpreta como HTML.
    public static string TextoPlanoAHtml(string texto) => string.Concat(
        texto.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(parrafo => $"<p>{EscaparHtml(parrafo).Replace("\n", "<br>")}</p>"));

    // Solo los caracteres con significado en HTML: «Pérez» se queda «Pérez» en el borrador.
    private static string EscaparHtml(string valor) => valor
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;").Replace("'", "&#39;");
}
