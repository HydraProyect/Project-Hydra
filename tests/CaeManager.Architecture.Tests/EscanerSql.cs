using System.Text.RegularExpressions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Escáner léxico secuencial de SQL de PostgreSQL para los tests de arquitectura que leen texto SQL
/// sin base de datos. Recorre el texto una sola vez y clasifica cada posición:
/// <list type="bullet">
/// <item>literal <c>'…'</c> (con <c>''</c> como comilla escapada) y <c>E'…'</c> (además con barra invertida);</item>
/// <item>cadena con dólar <c>$etiqueta$ … $etiqueta$</c> (la etiqueta puede ser vacía y llevar letras no ASCII);</item>
/// <item>comentario <c>--</c> hasta <c>\n</c> o <c>\r</c> y <c>/* */</c>, que en PostgreSQL se anida;</item>
/// <item>identificador entre comillas dobles <c>"…"</c> (con <c>""</c> escapada);</item>
/// <item>el resto es código.</item>
/// </list>
/// Lo que va dentro de una cadena con dólar (también el cuerpo <c>AS $$ … $$</c> de una función) es
/// un literal para quien lee la sentencia exterior; quien quiera leer ese cuerpo como código lo
/// extrae con <see cref="PartesDeFuncion"/> y lo vuelve a escanear.
/// </summary>
internal static class EscanerSql
{
    internal enum Clase { Codigo, Comentario, Literal, Identificador }

    internal readonly record struct Token(Clase Clase, int Inicio, int Fin, int ContenidoInicio, int ContenidoFin, bool ConDolar);

    private static bool EsDeIdentificador(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    /// <summary>Los tokens que no son código, en orden y sin solaparse.</summary>
    internal static List<Token> NoCodigo(string sql)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            var siguiente = i + 1 < sql.Length ? sql[i + 1] : '\0';
            var previoEsIdentificador = i > 0 && EsDeIdentificador(sql[i - 1]);

            if (c is 'E' or 'e' && siguiente == '\'' && !previoEsIdentificador)
            {
                var fin = FinDeLiteral(sql, i + 2, conBarra: true);
                tokens.Add(new Token(Clase.Literal, i, fin, i + 2, Math.Max(i + 2, fin - 1), false));
                i = fin;
            }
            else if (c == '\'')
            {
                var fin = FinDeLiteral(sql, i + 1, conBarra: false);
                tokens.Add(new Token(Clase.Literal, i, fin, i + 1, Math.Max(i + 1, fin - 1), false));
                i = fin;
            }
            else if (c == '"')
            {
                var fin = i + 1;
                while (fin < sql.Length)
                {
                    if (sql[fin] == '"')
                    {
                        if (fin + 1 < sql.Length && sql[fin + 1] == '"') { fin += 2; continue; }
                        fin++;
                        break;
                    }
                    fin++;
                }
                tokens.Add(new Token(Clase.Identificador, i, fin, i + 1, Math.Max(i + 1, fin - 1), false));
                i = fin;
            }
            else if (c == '$' && !previoEsIdentificador && EtiquetaDolar(sql, i) is { } etiqueta)
            {
                var contenido = i + etiqueta.Length;
                var cierre = sql.IndexOf(etiqueta, contenido, StringComparison.Ordinal);
                var fin = cierre < 0 ? sql.Length : cierre + etiqueta.Length;
                tokens.Add(new Token(Clase.Literal, i, fin, contenido, cierre < 0 ? sql.Length : cierre, true));
                i = fin;
            }
            else if (c == '-' && siguiente == '-')
            {
                // Como en scan.l (non_newline [^\n\r]): el comentario acaba en \n o en \r.
                var fin = sql.IndexOfAny(['\n', '\r'], i);
                fin = fin < 0 ? sql.Length : fin;
                tokens.Add(new Token(Clase.Comentario, i, fin, i, fin, false));
                i = fin;
            }
            else if (c == '/' && siguiente == '*')
            {
                var profundidad = 1;
                var fin = i + 2;
                while (fin < sql.Length && profundidad > 0)
                {
                    if (sql[fin] == '/' && fin + 1 < sql.Length && sql[fin + 1] == '*') { profundidad++; fin += 2; }
                    else if (sql[fin] == '*' && fin + 1 < sql.Length && sql[fin + 1] == '/') { profundidad--; fin += 2; }
                    else fin++;
                }
                tokens.Add(new Token(Clase.Comentario, i, fin, i, fin, false));
                i = fin;
            }
            else
            {
                i++;
            }
        }

        return tokens;
    }

    private static int FinDeLiteral(string sql, int desde, bool conBarra)
    {
        var fin = desde;
        while (fin < sql.Length)
        {
            if (conBarra && sql[fin] == '\\') { fin += 2; continue; }
            if (sql[fin] == '\'')
            {
                if (fin + 1 < sql.Length && sql[fin + 1] == '\'') { fin += 2; continue; }
                return fin + 1;
            }
            fin++;
        }

        return sql.Length;
    }

    // scan.l: dolq_start [A-Za-z\200-\377_] y dolq_cont [A-Za-z\200-\377_0-9]; los bytes \200-\377
    // son los de cualquier carácter no ASCII en UTF-8, aquí \u0080 en adelante.
    private static readonly Regex AperturaDolar = new(
        @"\G\$(?:[A-Za-z\u0080-￿_][A-Za-z\u0080-￿_0-9]*)?\$", RegexOptions.Compiled);

    private static string? EtiquetaDolar(string sql, int i)
    {
        var apertura = AperturaDolar.Match(sql, i);
        return apertura.Success ? apertura.Value : null;
    }

    /// <summary>La clase de cada posición del texto.</summary>
    internal static Clase[] Clasificar(string sql)
    {
        var clases = new Clase[sql.Length];
        foreach (var token in NoCodigo(sql))
            Array.Fill(clases, token.Clase, token.Inicio, token.Fin - token.Inicio);
        return clases;
    }

    /// <summary>El texto con los comentarios cambiados por espacios (misma longitud y posiciones).</summary>
    internal static string SinComentarios(string sql) => Enmascarar(sql, c => c == Clase.Comentario);

    /// <summary>El texto con todo lo que no es código cambiado por espacios (misma longitud y posiciones).</summary>
    internal static string SoloCodigo(string sql) => Enmascarar(sql, c => c != Clase.Codigo);

    private static string Enmascarar(string sql, Func<Clase, bool> fuera)
    {
        var clases = Clasificar(sql);
        var salida = sql.ToCharArray();
        for (var k = 0; k < salida.Length; k++)
            if (fuera(clases[k]) && salida[k] != '\n')
                salida[k] = ' ';
        return new string(salida);
    }

    /// <summary>
    /// Las coincidencias del patrón que empiezan en código: buscadas en el texto sin comentarios, se
    /// descartan las que empiezan dentro de un literal, una cadena con dólar o un identificador entre
    /// comillas. Un <c>PERFORM 'p_detalle NOT IN (...)'</c> no es una comprobación que se aplique.
    /// </summary>
    internal static IReadOnlyList<Match> CoincidenciasEnCodigo(Regex patron, string sql)
    {
        var clases = Clasificar(sql);
        return patron.Matches(SinComentarios(sql)).Where(m => m.Length > 0 && clases[m.Index] == Clase.Codigo).ToList();
    }

    /// <summary>Posición de la primera aparición de la construcción que empieza en código; -1 si no la hay.</summary>
    internal static int IndiceEnCodigo(string sql, string construccion) =>
        CoincidenciasEnCodigo(new Regex(Regex.Escape(construccion)), sql) is [var primera, ..] ? primera.Index : -1;

    /// <summary>
    /// La cabecera (desde <paramref name="desde"/> hasta el cuerpo) y el cuerpo (contenido de la primera
    /// cadena con dólar en código a partir de <paramref name="desde"/>) de una definición de función.
    /// </summary>
    internal static (string Cabecera, string Cuerpo, int FinDelCuerpo) PartesDeFuncion(string sql, int desde = 0)
    {
        var cuerpo = NoCodigo(sql).FirstOrDefault(t => t.ConDolar && t.Inicio >= desde);
        if (!cuerpo.ConDolar)
            throw new InvalidOperationException("La definición no tiene cuerpo entre dólares.");
        return (sql[desde..cuerpo.Inicio], sql[cuerpo.ContenidoInicio..cuerpo.ContenidoFin], cuerpo.Fin);
    }
}
