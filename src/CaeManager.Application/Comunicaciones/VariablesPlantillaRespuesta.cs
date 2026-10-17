using System.Net;
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
                ? WebUtility.HtmlEncode(valor)
                : m.Value);
}
