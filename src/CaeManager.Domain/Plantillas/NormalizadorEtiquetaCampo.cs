using System.Globalization;
using System.Text;

namespace CaeManager.Domain.Plantillas;

/// <summary>
/// Normaliza una etiqueta de campo (nombre de AcroForm, texto detectado por
/// IA) a la misma forma canónica con la que se siembra
/// <see cref="ConocimientoDeteccionCampo.EtiquetaNormalizada"/> — minúsculas,
/// sin acentos, sin puntuación de formulario ("CIF:" → "cif"), espacios
/// colapsados. Usado tanto al sembrar el conocimiento como al detectar
/// campos de una plantilla nueva, para que ambos lados comparen lo mismo.
/// </summary>
public static class NormalizadorEtiquetaCampo
{
    public static string Normalizar(string? etiqueta)
    {
        if (string.IsNullOrWhiteSpace(etiqueta))
            return string.Empty;

        var sinAcentos = QuitarAcentos(etiqueta.Trim().ToLowerInvariant());
        var soloAlfanumericoYEspacios = new string(sinAcentos
            .Select(c => char.IsLetterOrDigit(c) ? c : ' ')
            .ToArray());

        var palabras = soloAlfanumericoYEspacios.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', palabras);
    }

    private static string QuitarAcentos(string texto)
    {
        var normalizado = texto.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalizado)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
