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
}
