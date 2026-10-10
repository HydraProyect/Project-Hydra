using System.Globalization;
using System.Text;

namespace CaeManager.Application.Common;

/// <summary>
/// Comparación de texto de los buscadores de los listados: ignora acentos y mayúsculas
/// («garcia» encuentra «García», «GARCÍA» y «garcia»).
///
/// <see cref="Contiene"/> tiene dos implementaciones con el mismo contrato:
/// <list type="bullet">
/// <item>Dentro de una consulta de EF se traduce a SQL (lo mapea <c>CaeManagerDbContext</c> con
/// <c>HasDbFunction</c>):
/// <c>public.texto_de_busqueda(columna) LIKE public.patron_de_busqueda(término) ESCAPE '\'</c>.
/// Columna y término pasan los dos por PostgreSQL (<c>unaccent</c> y <c>upper</c>), nunca uno
/// por C# y el otro por SQL: las dos normalizaciones no coinciden en todos los caracteres
/// (<c>unaccent</c> convierte «ß» en «ss» y «ø» en «o»; la de C# no), y mezclarlas dejaría
/// de encontrar filas que el buscador anterior sí encontraba.</item>
/// <item>Fuera de una consulta, sobre una lista ya cargada en memoria, ejecuta el cuerpo de C#.</item>
/// </list>
///
/// <see cref="Coincidencias"/> dice DÓNDE casa, con el criterio en memoria, para resaltarlo en la fila.
/// </summary>
public static class TextoDeBusqueda
{
    /// <summary>Función SQL que normaliza una columna; es la expresión de los índices trigram de búsqueda.</summary>
    public const string FuncionSqlTexto = "texto_de_busqueda";

    /// <summary>Función SQL que normaliza el término y lo convierte en un patrón <c>LIKE</c> «contiene», con los comodines escapados.</summary>
    public const string FuncionSqlPatron = "patron_de_busqueda";

    /// <summary>
    /// ¿Contiene <paramref name="texto"/> el <paramref name="termino"/>, sin distinguir acentos ni
    /// mayúsculas? Un texto nulo no contiene nada. El término se toma literal: «%» y «_» no son comodines.
    /// </summary>
    public static bool Contiene(string? texto, string termino) =>
        texto is not null && Normalizar(texto).Contains(Normalizar(termino), StringComparison.Ordinal);

    /// <summary>
    /// Normalización en memoria: sin marcas diacríticas y en mayúsculas. Solo para comparar dos
    /// textos que pasan los dos por aquí; no reproduce carácter a carácter la de PostgreSQL.
    /// </summary>
    public static string Normalizar(string texto)
    {
        var descompuesto = texto.Normalize(NormalizationForm.FormD);
        var limpio = new StringBuilder(descompuesto.Length);
        foreach (var caracter in descompuesto)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(caracter) != UnicodeCategory.NonSpacingMark)
                limpio.Append(char.ToUpperInvariant(caracter));
        }

        return limpio.ToString();
    }

    /// <summary>
    /// Tramos de <paramref name="texto"/> que casan con <paramref name="termino"/> según el criterio
    /// en memoria de <see cref="Contiene"/> (misma <see cref="Normalizar"/>), como rangos sobre el
    /// texto ORIGINAL, de izquierda a derecha y sin solaparse (los contiguos se funden). Es lo que
    /// pinta el resaltado de coincidencias de los listados.
    ///
    /// Cada rango empieza y acaba en frontera de elemento de texto (grafema): «garcia» sobre
    /// «García» devuelve la palabra entera con su «í», esté precompuesta o descompuesta (NFD),
    /// y nunca parte un par sustituto ni deja fuera una marca combinante.
    ///
    /// Sin texto, sin término o sin coincidencia devuelve una lista vacía. Hereda la divergencia de
    /// <see cref="Normalizar"/> con PostgreSQL: una fila que la consulta encontró por una
    /// equivalencia que solo hace <c>unaccent</c> («strasse» → «Straße») no devuelve tramos.
    /// </summary>
    public static IReadOnlyList<Range> Coincidencias(string? texto, string? termino)
    {
        if (string.IsNullOrEmpty(texto) || string.IsNullOrEmpty(termino))
            return [];

        try
        {
            var buscado = Normalizar(termino);
            if (buscado.Length == 0)
                return [];

            // Se normaliza elemento a elemento para saber de qué grafema del original sale cada
            // carácter normalizado: un grafema puede dar uno («í» → «I»), varios o ninguno.
            var inicios = StringInfo.ParseCombiningCharacters(texto);
            var normalizado = new StringBuilder(texto.Length);
            var elementoDe = new List<int>(texto.Length);
            for (var elemento = 0; elemento < inicios.Length; elemento++)
            {
                var fin = elemento + 1 < inicios.Length ? inicios[elemento + 1] : texto.Length;
                var trozo = Normalizar(texto[inicios[elemento]..fin]);
                normalizado.Append(trozo);
                for (var i = 0; i < trozo.Length; i++)
                    elementoDe.Add(elemento);
            }

            var plano = normalizado.ToString();
            var tramos = new List<Range>();
            var desde = 0;
            while (desde + buscado.Length <= plano.Length)
            {
                var posicion = plano.IndexOf(buscado, desde, StringComparison.Ordinal);
                if (posicion < 0)
                    break;

                var inicio = inicios[elementoDe[posicion]];
                var ultimo = elementoDe[posicion + buscado.Length - 1];
                var fin = ultimo + 1 < inicios.Length ? inicios[ultimo + 1] : texto.Length;

                if (tramos.Count > 0 && tramos[^1].End.Value >= inicio)
                    tramos[^1] = tramos[^1].Start.Value..Math.Max(fin, tramos[^1].End.Value);
                else
                    tramos.Add(inicio..fin);

                desde = posicion + buscado.Length;
            }

            return tramos;
        }
        catch (ArgumentException)
        {
            // Un sustituto suelto no es Unicode válido y string.Normalize lo rechaza. El resaltado
            // es decoración: ante un texto así no se marca nada, no se rompe el render de la fila.
            return [];
        }
    }
}
