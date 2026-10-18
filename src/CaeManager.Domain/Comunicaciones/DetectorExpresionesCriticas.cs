using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace CaeManager.Domain.Comunicaciones;

public record CoincidenciaCritica(string Expresion, string Texto);

public static class DetectorExpresionesCriticas
{
    public static IReadOnlyList<string> ExpresionesPredeterminadas { get; } = Array.AsReadOnly(new[]
    {
        "inspección de trabajo", "inspección laboral", "inspecció de treball", "inspecció laboral",
        "sanción", "sanciones", "sanció", "sancions", "requerimiento oficial", "requeriment oficial",
        "denuncia", "denuncias", "denúncia", "denúncies", "acción legal", "acciones legales",
        "acció legal", "accions legals", "requerimiento de la inspección", "requeriment de la inspecció"
    });

    public static CoincidenciaCritica? Detectar(Mensaje mensaje, IReadOnlyList<string>? expresiones = null)
    {
        if (mensaje.Direccion != DireccionMensaje.Entrante) return null;
        var texto = WebUtility.HtmlDecode(Regex.Replace(mensaje.CuerpoHtml, "<[^>]*>", " ", RegexOptions.None, TimeSpan.FromSeconds(1)));
        var (normalizado, posiciones) = NormalizarConPosiciones(texto);
        foreach (var expresion in expresiones ?? ExpresionesPredeterminadas)
        {
            if (string.IsNullOrWhiteSpace(expresion) || expresion.Length > 200) continue;
            var expresionNormalizada = Normalizar(expresion);
            if (expresionNormalizada.Length == 0) continue;
            var patron = @"(?<![\p{L}\p{N}])" + Regex.Escape(expresionNormalizada) + @"(?![\p{L}\p{N}])";
            var coincidencia = Regex.Match(normalizado, patron, RegexOptions.None, TimeSpan.FromSeconds(1));
            if (!coincidencia.Success) continue;
            // El fragmento incluye siempre la coincidencia, también en mensajes largos.
            var inicio = Math.Max(0, posiciones[coincidencia.Index] - 150);
            return new CoincidenciaCritica(expresion, texto.Substring(inicio, Math.Min(4000, texto.Length - inicio)));
        }
        return null;
    }

    private static string Normalizar(string texto)
    {
        var resultado = new StringBuilder();
        foreach (var caracter in texto.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(caracter) != UnicodeCategory.NonSpacingMark)
                resultado.Append(char.ToLowerInvariant(caracter));
        }
        return Regex.Replace(resultado.ToString().Normalize(NormalizationForm.FormC), @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();
    }

    private static (string Texto, List<int> Posiciones) NormalizarConPosiciones(string texto)
    {
        var resultado = new StringBuilder(texto.Length);
        var posiciones = new List<int>(texto.Length);
        var elementos = StringInfo.GetTextElementEnumerator(texto);
        while (elementos.MoveNext())
        {
            foreach (var caracter in elementos.GetTextElement().Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(caracter) == UnicodeCategory.NonSpacingMark) continue;
                var normalizado = char.IsWhiteSpace(caracter) ? ' ' : char.ToLowerInvariant(caracter);
                if (normalizado == ' ' && (resultado.Length == 0 || resultado[^1] == ' ')) continue;
                resultado.Append(normalizado);
                posiciones.Add(elementos.ElementIndex);
            }
        }
        return (resultado.ToString(), posiciones);
    }
}
