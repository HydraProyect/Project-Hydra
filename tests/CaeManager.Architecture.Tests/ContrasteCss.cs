using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Instrumento compartido por los tests de contraste por tema
/// (<see cref="ContrasteDeAvataresPorTemaTests"/> y
/// <see cref="ContrasteDeSeleccionHoverYLogoPorTemaTests"/>): resuelve los
/// <c>var(--token)</c> de <c>tokens.css</c> por tema, lee las declaraciones de
/// una regla CSS y mide el contraste WCAG 2.x.
/// </summary>
internal static class ContrasteCss
{
    public sealed class Tokens
    {
        private readonly Dictionary<string, string> _raiz;
        private readonly Dictionary<string, Dictionary<string, string>> _temas;

        private Tokens(Dictionary<string, string> raiz, Dictionary<string, Dictionary<string, string>> temas)
        {
            _raiz = raiz;
            _temas = temas;
        }

        /// <summary>Los tres bloques (:root, oscuro, claro) existen y declaran algo.</summary>
        public bool Completo => _raiz.Count > 0 && _temas.Values.All(t => t.Count > 0);

        public static Tokens Desde(string css)
        {
            var texto = SinComentarios(css);
            return new Tokens(
                Declaraciones(Cuerpo(texto, @":root\s*\{")),
                new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
                {
                    ["oscuro"] = Declaraciones(Cuerpo(texto, @":root\[data-theme='oscuro'\]\s*\{")),
                    ["claro"] = Declaraciones(Cuerpo(texto, @":root\[data-theme='claro'\]\s*\{")),
                });
        }

        /// <summary>Valor <c>#rrggbb</c> de un color o de un <c>var(--token)</c>, con el tema por delante de :root.</summary>
        public string Resolver(string valor, string tema, int profundidad = 0)
        {
            if (profundidad > 20) throw new InvalidOperationException($"Cadena de var() sin fin resolviendo '{valor}'");

            valor = valor.Trim();
            var hex = Regex.Match(valor, @"^#([0-9a-fA-F]{6})$");
            if (hex.Success) return valor.ToLowerInvariant();

            var v = Regex.Match(valor, @"^var\(\s*(--[\w-]+)\s*\)$");
            if (!v.Success)
                throw new InvalidOperationException($"Formato de color no soportado por este test: '{valor}'");

            var nombre = v.Groups[1].Value;
            if (_temas[tema].TryGetValue(nombre, out var deTema)) return Resolver(deTema, tema, profundidad + 1);
            if (_raiz.TryGetValue(nombre, out var deRaiz)) return Resolver(deRaiz, tema, profundidad + 1);
            throw new InvalidOperationException($"El token {nombre} no está declarado en tokens.css");
        }

        /// <summary>
        /// Como <see cref="Resolver"/> pero sin lanzar: <c>null</c> si el valor no es un
        /// <c>#rrggbb</c> ni un <c>var()</c> que llegue a uno (<c>inherit</c>, <c>rgba()</c>,
        /// <c>none</c>, un token sin declarar...). Para barridos del árbol entero, donde un
        /// formato que el instrumento no entiende no puede detener la medida de los demás.
        /// </summary>
        public string? IntentarResolver(string valor, string tema, int profundidad = 0)
        {
            if (profundidad > 20) return null;

            valor = valor.Trim();
            if (Regex.IsMatch(valor, @"^#[0-9a-fA-F]{6}$")) return valor.ToLowerInvariant();
            var corto = Regex.Match(valor, @"^#([0-9a-fA-F])([0-9a-fA-F])([0-9a-fA-F])$");
            if (corto.Success)
                return $"#{corto.Groups[1].Value}{corto.Groups[1].Value}{corto.Groups[2].Value}{corto.Groups[2].Value}" +
                       $"{corto.Groups[3].Value}{corto.Groups[3].Value}".ToLowerInvariant();
            if (valor.Equals("white", StringComparison.OrdinalIgnoreCase)) return "#ffffff";
            if (valor.Equals("black", StringComparison.OrdinalIgnoreCase)) return "#000000";

            // var(--x) y var(--x, #fallback): sin declaración del token manda el fallback, como en el navegador.
            var v = Regex.Match(valor, @"^var\(\s*(--[\w-]+)\s*(?:,\s*(.+?))?\s*\)$");
            if (!v.Success) return null;

            var nombre = v.Groups[1].Value;
            if (_temas[tema].TryGetValue(nombre, out var deTema)) return IntentarResolver(deTema, tema, profundidad + 1);
            if (_raiz.TryGetValue(nombre, out var deRaiz)) return IntentarResolver(deRaiz, tema, profundidad + 1);
            return v.Groups[2].Success ? IntentarResolver(v.Groups[2].Value, tema, profundidad + 1) : null;
        }

        /// <summary>
        /// Color final de un fondo que puede ser translúcido, ya compuesto sobre
        /// <paramref name="detras"/> (<c>#rrggbb</c>). Entiende <c>#rrggbb</c>, <c>var()</c> y
        /// <c>color-mix(in srgb, COLOR N%, transparent)</c>: el que usa la caja del editor de
        /// plantillas para dejar ver el PDF. <c>null</c> si el valor no es de esos formatos.
        /// </summary>
        public string? ResolverSobre(string valor, string tema, string detras)
        {
            valor = valor.Trim();
            var mezcla = Regex.Match(valor,
                @"^color-mix\(\s*in\s+srgb\s*,\s*(.+?)\s+(\d+(?:\.\d+)?)%\s*,\s*transparent\s*\)$");
            if (!mezcla.Success)
            {
                // El valor de un token puede ser, a su vez, un color-mix(): se sigue la cadena de var().
                var v = Regex.Match(valor, @"^var\(\s*(--[\w-]+)\s*(?:,\s*(.+?))?\s*\)$");
                if (!v.Success) return IntentarResolver(valor, tema);
                var nombre = v.Groups[1].Value;
                if (_temas[tema].TryGetValue(nombre, out var deTema)) return ResolverSobre(deTema, tema, detras);
                if (_raiz.TryGetValue(nombre, out var deRaiz)) return ResolverSobre(deRaiz, tema, detras);
                return v.Groups[2].Success ? ResolverSobre(v.Groups[2].Value, tema, detras) : null;
            }

            var color = IntentarResolver(mezcla.Groups[1].Value, tema);
            if (color is null) return null;
            var alfa = double.Parse(mezcla.Groups[2].Value, CultureInfo.InvariantCulture) / 100.0;
            return Componer(color, alfa, detras);
        }

        private static string Componer(string arriba, double alfa, string abajo)
        {
            int Canal(string hex, int desde) =>
                int.Parse(hex.AsSpan(desde, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

            string Mezcla(int desde) =>
                ((int)Math.Round(Canal(arriba, desde) * alfa + Canal(abajo, desde) * (1 - alfa)))
                .ToString("x2", CultureInfo.InvariantCulture);

            return "#" + Mezcla(1) + Mezcla(3) + Mezcla(5);
        }

        private static string Cuerpo(string texto, string apertura)
        {
            var m = Regex.Match(texto, apertura);
            if (!m.Success) return string.Empty;
            var inicio = m.Index + m.Length;
            var fin = texto.IndexOf('}', inicio);
            fin.Should().BeGreaterThan(-1);
            return texto[inicio..fin];
        }

        private static Dictionary<string, string> Declaraciones(string cuerpo)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(cuerpo, @"(--[\w-]+)\s*:\s*([^;]+);"))
                d[m.Groups[1].Value] = m.Groups[2].Value.Trim();
            return d;
        }
    }

    /// <summary>
    /// <c>background</c> y <c>color</c> declarados por la regla del primer selector y, encima,
    /// los de las variantes siguientes (la última declaración gana). <c>null</c> si ninguna los declara.
    /// </summary>
    internal static (string? Fondo, string? Texto) ColoresDeLaRegla(string css, string[] selectores)
    {
        var texto = SinComentarios(css);
        string? fondo = null, letra = null;

        foreach (var selector in selectores)
        {
            var m = Regex.Match(texto, @"(?<![\w-])" + Regex.Escape(selector) + @"\s*\{([^}]*)\}");
            m.Success.Should().BeTrue($"el CSS debe tener una regla '{selector} {{ ... }}' (lista de reglas desfasada)");
            var cuerpo = m.Groups[1].Value;

            var f = Regex.Match(cuerpo, @"(?<![-\w])background(?:-color)?\s*:\s*([^;]+)");
            if (f.Success) fondo = f.Groups[1].Value.Trim();
            var c = Regex.Match(cuerpo, @"(?<![-\w])color\s*:\s*([^;]+)");
            if (c.Success) letra = c.Groups[1].Value.Trim();
        }

        return (fondo, letra);
    }

    /// <summary><c>background</c> y <c>color</c> declarados en el cuerpo de una regla (<c>null</c> si no los declara).</summary>
    internal static (string? Fondo, string? Texto) ColoresDelCuerpo(string cuerpo)
    {
        var f = Regex.Match(cuerpo, @"(?<![-\w])background(?:-color)?\s*:\s*([^;]+)");
        var c = Regex.Match(cuerpo, @"(?<![-\w])color\s*:\s*([^;]+)");
        static string SinImportant(string v) => Regex.Replace(v.Trim(), @"\s*!important$", string.Empty);
        return (f.Success ? SinImportant(f.Groups[1].Value) : null, c.Success ? SinImportant(c.Groups[1].Value) : null);
    }

    /// <summary>Una regla del CSS con un único selector (las listas separadas por comas se reparten).</summary>
    internal sealed record Regla(string Selector, string Cuerpo);

    /// <summary>
    /// Todas las reglas de un CSS, también las anidadas en <c>@media</c>, con su selector
    /// normalizado en espacios y las listas de selectores repartidas una regla por selector.
    /// No entiende CSS anidado (<c>&amp; { }</c>); ninguna hoja del repositorio lo usa (medido 2026-10-02).
    /// </summary>
    internal static IEnumerable<Regla> Reglas(string css)
    {
        foreach (Match m in Regex.Matches(SinComentarios(css), @"([^{}]+)\{([^{}]*)\}"))
        {
            var cabecera = m.Groups[1].Value.Trim();
            if (cabecera.StartsWith('@')) continue;
            foreach (var selector in DividirSelectores(cabecera))
                yield return new Regla(selector, m.Groups[2].Value);
        }
    }

    private static IEnumerable<string> DividirSelectores(string lista)
    {
        var profundidad = 0;
        var inicio = 0;
        for (var i = 0; i <= lista.Length; i++)
        {
            if (i < lista.Length)
            {
                if (lista[i] == '(') profundidad++;
                else if (lista[i] == ')') profundidad--;
                if (lista[i] != ',' || profundidad > 0) continue;
            }

            var s = Regex.Replace(lista[inicio..i].Trim(), @"\s+", " ");
            if (s.Length > 0) yield return s;
            inicio = i + 1;
        }
    }

    /// <summary>Un selector sin sus estados (<c>:hover</c>, <c>:active</c>, <c>:focus</c>, <c>:focus-visible</c>, <c>:focus-within</c>, <c>:not(:disabled)</c>).</summary>
    internal static string SinEstados(string selector) =>
        Regex.Replace(selector, @":not\(:disabled\)|:hover|:active|:focus-visible|:focus-within|:focus(?![\w-])", string.Empty);

    /// <summary>Contraste WCAG 2.x entre dos colores <c>#rrggbb</c>.</summary>
    internal static double Contraste(string a, string b)
    {
        var (la, lb) = (Luminancia(a), Luminancia(b));
        if (la < lb) (la, lb) = (lb, la);
        return (la + 0.05) / (lb + 0.05);
    }

    internal static double Luminancia(string hex)
    {
        double Canal(int desde)
        {
            var c = int.Parse(hex.AsSpan(desde, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Canal(1) + 0.7152 * Canal(3) + 0.0722 * Canal(5);
    }

    internal static string SinComentarios(string css) =>
        Regex.Replace(css, @"/\*.*?\*/", m => new string(m.Value.Select(c => c == '\n' ? '\n' : ' ').ToArray()),
            RegexOptions.Singleline);

    internal static string RutaTokensCss() => Path.Combine(RaizWeb(), "wwwroot", "css", "tokens.css");

    internal static string RaizWeb() => Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web");

    internal static string RaizDelRepositorio()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? AppContext.BaseDirectory;
    }
}
