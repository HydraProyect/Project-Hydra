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
