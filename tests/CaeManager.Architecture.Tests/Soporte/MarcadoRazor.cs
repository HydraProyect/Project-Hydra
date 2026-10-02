using System.Text.RegularExpressions;

namespace CaeManager.Architecture.Tests.Soporte;

/// <summary>
/// Lectura mínima del marcado de los <c>.razor</c> para los trinquetes de componentes (un solo primario
/// por superficie, guardián de cambios en Drawer y Modal). No es un analizador de Razor: delimita
/// etiquetas de apertura y elementos con anidamiento del mismo nombre, y respeta lo que de verdad
/// rompe a un regex ingenuo dentro de un atributo (comillas, <c>=&gt;</c> de una lambda, paréntesis,
/// cadenas y literales de carácter de las expresiones <c>@(...)</c>). Quien lo use debe pasarle el
/// texto ya sin comentarios (<see cref="LimpiadorDeComentarios"/>) y comprobar con un control positivo
/// que ve lo que dice ver: un recorrido que no encuentra nada da verde por vacío.
/// </summary>
public static class MarcadoRazor
{
    /// <summary>
    /// Un elemento hallado: <paramref name="Apertura"/> es la etiqueta de apertura completa (con sus
    /// atributos), <paramref name="Cuerpo"/> lo que hay entre apertura y cierre (vacío si es
    /// autocerrado), <paramref name="Ordinal"/> el número de orden entre los de su nombre en el fichero
    /// (contando también los autocerrados), de 1 en adelante.
    /// </summary>
    public sealed record Elemento(string Nombre, int Ordinal, int Inicio, int Fin, string Apertura, string Cuerpo, bool Autocerrado);

    /// <summary>Los elementos cuyo nombre casa con <paramref name="nombres"/> (alternativa de regex), en orden de aparición.</summary>
    public static List<Elemento> Elementos(string texto, string nombres)
    {
        var apertura = new Regex($@"<({nombres})(?=[\s>/])");
        var resultado = new List<Elemento>();
        var ordinales = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (Match m in apertura.Matches(texto))
        {
            var nombre = m.Groups[1].Value;
            ordinales[nombre] = ordinales.GetValueOrDefault(nombre) + 1;
            var finApertura = FinDeEtiqueta(texto, m.Index + m.Length);
            var etiqueta = texto[m.Index..(finApertura + 1)];

            if (texto[finApertura - 1] == '/')
            {
                resultado.Add(new Elemento(nombre, ordinales[nombre], m.Index, finApertura + 1, etiqueta, string.Empty, true));
                continue;
            }

            var fin = CierreDe(texto, nombre, finApertura + 1);
            var cierre = $"</{nombre}>";
            var finCuerpo = fin >= cierre.Length && fin <= texto.Length && string.CompareOrdinal(texto, fin - cierre.Length, cierre, 0, cierre.Length) == 0
                ? fin - cierre.Length
                : fin;
            resultado.Add(new Elemento(nombre, ordinales[nombre], m.Index, fin, etiqueta, texto[(finApertura + 1)..finCuerpo], false));
        }

        return resultado;
    }

    /// <summary>Una etiqueta de apertura (sin buscar su cierre: sirve también para elementos vacíos como <c>&lt;input&gt;</c>).</summary>
    public sealed record Apertura(string Nombre, int Inicio, string Texto);

    /// <summary>Las etiquetas de apertura cuyo nombre casa con <paramref name="nombres"/> (alternativa de regex), en orden de aparición.</summary>
    public static List<Apertura> Aperturas(string texto, string nombres)
    {
        var patron = new Regex($@"<({nombres})(?=[\s>/])");
        return patron.Matches(texto)
            .Select(m => new Apertura(m.Groups[1].Value, m.Index, texto[m.Index..(FinDeEtiqueta(texto, m.Index + m.Length) + 1)]))
            .ToList();
    }

    /// <summary>Posición del <c>&gt;</c> que cierra una etiqueta de apertura; respeta comillas y expresiones con paréntesis.</summary>
    public static int FinDeEtiqueta(string s, int desde)
    {
        var j = desde;
        while (j < s.Length)
        {
            if (s[j] == '"')
                j = FinDeComillas(s, j + 1) + 1;
            else if (s[j] == '>')
                return j;
            else
                j++;
        }

        return s.Length - 1;
    }

    /// <summary>Posición de la comilla que cierra un valor de atributo que empieza en <paramref name="desde"/> (tras la comilla de apertura).</summary>
    public static int FinDeComillas(string s, int desde)
    {
        var j = desde;
        var profundidad = 0;
        while (j < s.Length)
        {
            var c = s[j];
            if (profundidad == 0 && c == '"') return j;
            if (c is '(' or '{' or '[') profundidad++;
            else if (c is ')' or '}' or ']') profundidad--;
            else if (c == '"' && profundidad > 0)
            {
                // Cadena dentro de una expresión: se salta entera, con sus escapes.
                j++;
                while (j < s.Length && s[j] != '"')
                {
                    if (s[j] == '\\') j++;
                    j++;
                }
            }
            else if (c == '\'' && profundidad > 0)
            {
                // Literal de carácter dentro de una expresión ('x', '\n', ')'): su contenido no cuenta para los paréntesis.
                j++;
                if (j < s.Length && s[j] == '\\') j++;
                j++;
            }

            j++;
        }

        return s.Length - 1;
    }

    /// <summary>El valor de un atributo entre comillas que empieza en <paramref name="desde"/> (tras la comilla de apertura).</summary>
    public static string ValorDeComillas(string s, int desde) => s[desde..FinDeComillas(s, desde)];

    /// <summary>Fin (exclusivo) del elemento <c>&lt;Nombre&gt;…&lt;/Nombre&gt;</c> abierto justo antes de <paramref name="desde"/>, con anidamiento del mismo nombre.</summary>
    public static int CierreDe(string s, string nombre, int desde)
    {
        var patron = new Regex($@"<{nombre}(?=[\s>/])|</{nombre}>");
        var profundidad = 1;
        var j = desde;
        while (profundidad > 0)
        {
            var m = patron.Match(s, j);
            if (!m.Success) return s.Length;
            if (m.Value.StartsWith("</", StringComparison.Ordinal)) profundidad--;
            else if (s[FinDeEtiqueta(s, m.Index + m.Length) - 1] != '/') profundidad++;
            j = m.Index + m.Length;
        }

        return j;
    }

    /// <summary>Los <c>.razor</c> de <c>src/CaeManager.Web</c> con su ruta desde la raíz del repositorio (con <c>/</c>) y su texto tal cual.</summary>
    public static IEnumerable<(string Ruta, string Contenido)> LeerRazorDeLaWeb()
    {
        var raiz = RaizDelRepositorio();
        var web = Path.Combine(raiz, "src", "CaeManager.Web");
        var sep = Path.DirectorySeparatorChar;

        return Directory
            .EnumerateFiles(web, "*.razor", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (Ruta: Path.GetRelativePath(raiz, f).Replace('\\', '/'), Contenido: File.ReadAllText(f)));
    }

    public static string RaizDelRepositorio()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? AppContext.BaseDirectory;
    }
}
