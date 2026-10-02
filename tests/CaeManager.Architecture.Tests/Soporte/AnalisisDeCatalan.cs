using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Pares clave de <c>.resx</c> cuyo valor ca-ES es <b>idéntico</b> al es-ES (el neutral). Un valor
/// catalán copiado del castellano no falla en ningún sitio —<c>ResourceManager</c> no sabe que
/// debería estar traducido—, así que nada en build, test o CI lo vería (S7 del análisis de causas
/// raíz de 2026-10-02, clase C8).
///
/// <para>
/// <b>Qué cuenta.</b> Solo claves presentes en los dos ficheros (la paridad de claves la exige
/// <c>LocalizacionRecursosYRegistroTests</c>), con el valor idéntico tras recortar espacios y quitar
/// los marcadores <c>{…}</c>, y con más de <see cref="MinimoDeLetras"/> letras: debajo de ese
/// umbral están las palabras sueltas y siglas que se escriben igual en las dos lenguas
/// («Inicio», «Sí», «Excel»). Un valor que sea legítimamente idéntico se declara en
/// <c>Congelados/ca-ES-iguales-validos.txt</c> (el valor normalizado, una línea cada uno, con el
/// motivo en un comentario): es una lista aparte de la deuda, no una entrada más de ella.
/// </para>
///
/// <para>
/// <b>Qué no ve.</b> Un valor catalán traducido a medias, una traducción incorrecta o un valor que
/// difiere solo en mayúsculas o espacios internos (no se normaliza más que lo dicho), y los
/// textos que no están en <c>.resx</c>.
/// </para>
/// </summary>
internal static class AnalisisDeCatalan
{
    public const int MinimoDeLetras = 10;

    private static readonly Regex Marcador = new(@"\{[^}]*\}", RegexOptions.Compiled);
    private static readonly Regex Espacios = new(@"\s+", RegexOptions.Compiled);

    public static string Normalizar(string valor) =>
        Espacios.Replace(Marcador.Replace(valor, string.Empty), " ").Trim();

    public static bool EsIgualYLargo(string neutral, string catalan, IReadOnlySet<string> validos)
    {
        if (neutral.Trim() != catalan.Trim())
            return false;

        var normalizado = Normalizar(neutral);
        return normalizado.Count(char.IsLetter) > MinimoDeLetras && !validos.Contains(normalizado);
    }

    public static IReadOnlySet<string> LeerValidos(string texto) =>
        texto.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);

    public static Dictionary<Ubicacion, int> Medir(
        IEnumerable<(string Ruta, string Neutral, string Catalan)> pares,
        IReadOnlySet<string> validos)
    {
        var resultado = new Dictionary<Ubicacion, int>();

        foreach (var (ruta, textoNeutral, textoCatalan) in pares)
        {
            var neutral = Valores(textoNeutral);
            var catalan = Valores(textoCatalan);

            foreach (var (clave, valor) in neutral)
            {
                if (catalan.TryGetValue(clave, out var valorCatalan) && EsIgualYLargo(valor, valorCatalan, validos))
                    resultado[new Ubicacion(ruta, clave)] = 1;
            }
        }

        return resultado;
    }

    private static Dictionary<string, string> Valores(string textoResx) =>
        XDocument.Parse(textoResx).Root!
            .Elements("data")
            .Select(d => (Clave: (string?)d.Attribute("name"), Valor: d.Element("value")?.Value))
            .Where(p => p.Clave is not null && p.Valor is not null)
            .GroupBy(p => p.Clave!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Valor!, StringComparer.Ordinal);

    /// <summary>Pares neutral/catalán que hay en <c>src</c> (solo los neutrales con su satélite al lado).</summary>
    public static IEnumerable<(string Ruta, string Neutral, string Catalan)> ParesDeSrc()
    {
        foreach (var archivo in FuentesDeSrc.Archivos().Where(a => a.EndsWith(".resx", StringComparison.OrdinalIgnoreCase)))
        {
            var satelite = Path.ChangeExtension(archivo, ".ca-ES.resx");
            if (File.Exists(satelite))
                yield return (FuentesDeSrc.Relativa(archivo), File.ReadAllText(archivo), File.ReadAllText(satelite));
        }
    }
}
