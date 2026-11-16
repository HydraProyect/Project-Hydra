using System.Globalization;
using System.Text;

namespace CaeManager.Domain.Inbound.Requisitos;

/// <summary>
/// «¿Es alguno de estos?» (DECISION_LOG 2026-11-14, requisito propio): antes de crear un requisito propio, el Gestor CAE
/// del Tenant TCE ve los tipos y requisitos de base que se parecen a lo que escribe, para no duplicar uno que ya tiene. Se
/// compara contra el nombre y los alias de cada candidato (D-28: los alias de <c>TipoDocumentoAlias</c>).
///
/// <para>
/// Solo sugiere: no impide crear el requisito ni decide equivalencias. Es una comparación de texto barata y determinista
/// (sin acentos, sin palabras vacías, prefijos y una errata por palabra); la semántica, si llega, es de la IA (B-25).
/// </para>
/// </summary>
public static class SimilitudNombresRequisito
{
    /// <summary>Puntuación mínima para sugerir un candidato.</summary>
    public const double UmbralSugerencia = 0.5;

    private const int LongitudMinimaPrefijo = 5;

    private static readonly HashSet<string> PalabrasVacias = new(StringComparer.Ordinal)
    {
        "de", "del", "la", "las", "el", "los", "y", "e", "o", "u", "en", "a", "al", "para", "por", "con", "sin", "un", "una",
        "d", "i", "les", "els", "amb", "per",
        "of", "the", "and", "or", "for", "in", "to",
    };

    /// <summary>
    /// De 0 a 1: 1 si los textos normalizados coinciden, 0,9 si uno contiene al otro y, si no, la proporción de palabras
    /// del texto más corto que casan con alguna del otro.
    /// </summary>
    public static double Puntuar(string texto, string candidato)
    {
        var a = Palabras(texto);
        var b = Palabras(candidato);
        if (a.Count == 0 || b.Count == 0)
            return 0;

        var frasesA = string.Join(' ', a);
        var frasesB = string.Join(' ', b);
        if (frasesA == frasesB)
            return 1;
        if (Contiene(frasesA, frasesB) || Contiene(frasesB, frasesA))
            return 0.9;

        var (corto, largo) = a.Count <= b.Count ? (a, b) : (b, a);
        var casan = corto.Count(p => largo.Any(q => Casan(p, q)));
        return (double)casan / corto.Count;
    }

    /// <summary>La mejor puntuación del texto contra un nombre y sus alias.</summary>
    public static double PuntuarContra(string texto, string nombre, IEnumerable<string> alias) =>
        alias.Prepend(nombre).Select(c => Puntuar(texto, c)).DefaultIfEmpty(0).Max();

    internal static List<string> Palabras(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto))
            return [];

        var descompuesto = texto.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var limpio = new StringBuilder(descompuesto.Length);
        foreach (var c in descompuesto)
        {
            var categoria = CharUnicodeInfo.GetUnicodeCategory(c);
            if (categoria == UnicodeCategory.NonSpacingMark)
                continue;
            limpio.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        return limpio.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !PalabrasVacias.Contains(p))
            .ToList();
    }

    private static bool Contiene(string frase, string parte) =>
        $" {frase} ".Contains($" {parte} ", StringComparison.Ordinal);

    private static bool Casan(string a, string b)
    {
        if (a == b)
            return true;
        var prefijo = Math.Min(a.Length, b.Length);
        if (prefijo >= LongitudMinimaPrefijo && string.CompareOrdinal(a, 0, b, 0, prefijo) == 0)
            return true;
        return Math.Min(a.Length, b.Length) >= 4 && DistanciaHastaUno(a, b);
    }

    /// <summary>Si la distancia de edición entre dos palabras es como mucho 1 (una errata).</summary>
    private static bool DistanciaHastaUno(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1)
            return false;
        var (corta, larga) = a.Length <= b.Length ? (a, b) : (b, a);
        int i = 0, j = 0, diferencias = 0;
        while (i < corta.Length && j < larga.Length)
        {
            if (corta[i] == larga[j])
            {
                i++;
                j++;
                continue;
            }
            if (++diferencias > 1)
                return false;
            if (corta.Length == larga.Length)
                i++;
            j++;
        }
        return diferencias + (larga.Length - j) + (corta.Length - i) <= 1;
    }
}
