using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Un elemento creado con <c>RenderTreeBuilder</c> (<c>builder.OpenElement</c>)
/// <b>no recibe</b> el atributo de ámbito del CSS aislado de Blazor: el
/// compilador de Razor solo lo añade a los elementos escritos como marcado. Un
/// selector de <c>Componente.razor.css</c> cuyo compuesto con ámbito apunta a
/// la clase de ese elemento —<c>.tarjeta-titulo</c> se compila a
/// <c>.tarjeta-titulo[b-…]</c>— no casa con nada.
///
/// <para>
/// <b>No falla, no avisa y bUnit no lo ve</b> (no aplica CSS). Medido el
/// 2026-10-10 en navegador: el título de <c>Tarjeta</c> llevaba un año sin su
/// <c>--font-heading-md</c>/<c>--font-heading-sm</c> (heredaba el h2 global), y
/// el círculo de <c>IndicadorPasos</c> no recibía ninguna de sus cuatro reglas.
/// </para>
///
/// <para>
/// <b>Contrato efectivo, más estrecho que el nombre.</b> Mira las clases
/// literales de <c>AddAttribute(n, "class", …)</c> en <c>X.razor</c> y
/// <c>X.razor.cs</c>, y los selectores de <c>X.razor.css</c>. El compuesto con
/// ámbito es el último del selector, o el anterior a <c>::deep</c> si lo hay.
/// NO ve clases compuestas en ejecución, ni elementos de un
/// <c>MarkupString</c>, ni un fragmento construido en una clase base o en otro
/// fichero, ni el estilo calculado. Un selector que alcanza la clase tras
/// <c>::deep</c>, o desde un <c>.css</c> global, es correcto y no se marca.
/// </para>
/// </summary>
public class SelectoresAisladosNoApuntanAElementosDeRenderTreeBuilderTests
{
    [Fact]
    public void Ningun_selector_aislado_apunta_a_una_clase_creada_con_RenderTreeBuilder()
    {
        var hojas = HojasAisladasConSuComponente();

        hojas.Should().NotBeEmpty(
            "sin ningún .razor.css localizado, este trinquete estaría en verde por no mirar nada");

        var infracciones = new List<string>();

        foreach (var (css, fuentes) in hojas)
        {
            var clasesDeBuilder = fuentes
                .SelectMany(f => ClasesCreadasConBuilder(File.ReadAllText(f)))
                .ToHashSet(StringComparer.Ordinal);
            if (clasesDeBuilder.Count == 0) continue;

            foreach (var (selector, clase) in SelectoresQueNoCasan(File.ReadAllText(css), clasesDeBuilder))
                infracciones.Add($"{Path.GetFileName(css)}: «{selector}» apunta a .{clase}");
        }

        string.Join("\n", infracciones.Distinct().OrderBy(x => x)).Should().BeEmpty(
            "un elemento creado con builder.OpenElement no lleva el atributo de ámbito, así que la regla "
            + "aislada no le llega y el elemento se pinta con el estilo global. Escribe el elemento como "
            + "marcado en el .razor, o alcánzalo con ::deep desde un ancestro declarado en el marcado");
    }

    /// <summary>
    /// Prueba de que el instrumento mira donde dice mirar: ve las hojas del
    /// sistema de diseño y de las features, empareja el code-behind, y hay en
    /// el repositorio al menos una clase creada con builder que extraer.
    /// </summary>
    [Fact]
    public void El_escaner_encuentra_hojas_aisladas_code_behind_y_clases_de_builder()
    {
        var hojas = HojasAisladasConSuComponente();

        hojas.Select(h => Path.GetFileName(h.Css))
            .Should().Contain("Tarjeta.razor.css").And.Contain("Centros.razor.css");

        var centros = hojas.Single(h => Path.GetFileName(h.Css) == "Centros.razor.css");
        centros.Fuentes.Select(Path.GetFileName)
            .Should().Contain("Centros.razor").And.Contain("Centros.razor.cs");

        centros.Fuentes.SelectMany(f => ClasesCreadasConBuilder(File.ReadAllText(f)))
            .Should().Contain("ventana-grupo",
                "Centros.razor.cs pinta esa clase con builder.OpenElement (su regla es global, en "
                + "list-page.css); si deja de hacerlo, pon aquí otro caso real para que la extracción "
                + "siga probada contra el árbol");
    }

    /// <summary>
    /// Sensibilidad permanente: el defecto exacto que se corrigió en
    /// <c>Tarjeta</c> sigue reconociéndose aunque el árbol ya no lo contenga.
    /// </summary>
    [Fact]
    public void El_detector_reconoce_el_defecto_del_titulo_de_Tarjeta()
    {
        const string razor = """
            private RenderFragment TituloTarjeta => builder =>
            {
                builder.OpenElement(0, $"h{NivelTitulo}");
                builder.AddAttribute(1, "class", "tarjeta-titulo");
                builder.AddContent(2, Titulo);
                builder.CloseElement();
            };
            """;
        const string css = """
            .tarjeta-header { display: flex; }
            .tarjeta-titulo { font: var(--font-heading-md); }
            .tarjeta-compacta .tarjeta-titulo,
            .tarjeta-titulo:hover { margin: 0; }
            .tarjeta-titulo .icono { margin: 0; }
            .tarjeta-header ::deep .tarjeta-titulo { margin: 0; }
            .tarjeta-titulo ::deep .icono { margin: 0; }
            .tarjeta-titulo-largo { margin: 0; }
            """;

        var clases = ClasesCreadasConBuilder(razor).ToHashSet(StringComparer.Ordinal);
        clases.Should().BeEquivalentTo(["tarjeta-titulo"]);

        SelectoresQueNoCasan(css, clases).Select(i => i.Selector).Should().BeEquivalentTo(
            [
                ".tarjeta-titulo",
                ".tarjeta-compacta .tarjeta-titulo",
                ".tarjeta-titulo:hover",
                ".tarjeta-titulo ::deep .icono",
            ],
            "el ámbito cae en el último compuesto, o en el anterior a ::deep; la clase como ancestro "
            + "sin ámbito, tras ::deep, o como prefijo de otro nombre, sí casa");
    }

    [Fact]
    public void El_detector_extrae_las_clases_de_una_expresion_condicional()
    {
        const string fuente = """
            builder.AddAttribute(
                1,
                "class",
                Integrada ? "titulo-panel uno" : "titulo-pagina");
            builder.AddAttribute(2, "aria-hidden", "true");
            """;

        ClasesCreadasConBuilder(fuente).Should().BeEquivalentTo(["titulo-panel", "uno", "titulo-pagina"]);
    }

    /// <summary>
    /// Selectores de <paramref name="css"/> cuyo compuesto con ámbito lleva una
    /// de las clases que solo existen en elementos creados con builder.
    /// </summary>
    private static IEnumerable<(string Selector, string Clase)> SelectoresQueNoCasan(
        string css, IReadOnlySet<string> clasesDeBuilder)
    {
        foreach (var bloque in BloquesDeSelector(css))
        {
            foreach (var selector in bloque.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0))
            {
                var compuesto = CompuestoConAmbito(selector);
                if (compuesto is null) continue;

                foreach (Match m in Regex.Matches(compuesto, @"\.(-?[_a-zA-Z][\w-]*)"))
                {
                    if (clasesDeBuilder.Contains(m.Groups[1].Value))
                        yield return (selector, m.Groups[1].Value);
                }
            }
        }
    }

    /// <summary>
    /// El compuesto al que Blazor añade el atributo de ámbito: el último del
    /// selector, o el que precede a <c>::deep</c>. Nulo si el selector empieza
    /// por <c>::deep</c> (no hay compuesto propio que marcar).
    /// </summary>
    private static string? CompuestoConAmbito(string selector)
    {
        var deep = selector.IndexOf("::deep", StringComparison.Ordinal);
        var propio = (deep >= 0 ? selector[..deep] : selector).Trim();
        if (propio.Length == 0) return null;

        return Regex.Split(propio, @"[\s>+~]+").LastOrDefault(c => c.Length > 0);
    }

    /// <summary>Clases literales de <c>AddAttribute(n, "class", …)</c>.</summary>
    private static IEnumerable<string> ClasesCreadasConBuilder(string fuente)
    {
        foreach (Match m in Regex.Matches(
            fuente, @"AddAttribute\(\s*\d+\s*,\s*""class""\s*,(.*?)\)\s*;", RegexOptions.Singleline))
        {
            foreach (Match literal in Regex.Matches(m.Groups[1].Value, @"""([^""]*)"""))
                foreach (var clase in literal.Groups[1].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    yield return clase;
        }
    }

    private static IEnumerable<string> BloquesDeSelector(string css)
    {
        var sinComentarios = Regex.Replace(css, @"/\*.*?\*/", " ", RegexOptions.Singleline);

        foreach (Match m in Regex.Matches(sinComentarios, @"(?<=^|[}{;])([^{};]*)\{"))
        {
            var selector = m.Groups[1].Value;
            if (selector.TrimStart().StartsWith('@')) continue;
            yield return selector;
        }
    }

    /// <summary>Cada <c>X.razor.css</c> con <c>X.razor</c> y, si existe, <c>X.razor.cs</c>.</summary>
    private static List<(string Css, List<string> Fuentes)> HojasAisladasConSuComponente()
    {
        var carpeta = Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web");
        if (!Directory.Exists(carpeta)) return [];

        return Directory
            .EnumerateFiles(carpeta, "*.razor.css", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(css =>
            {
                var razor = css[..^".css".Length];
                return (Css: css, Fuentes: new[] { razor, razor + ".cs" }.Where(File.Exists).ToList());
            })
            .ToList();
    }

    private static string RaizDelRepositorio()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? AppContext.BaseDirectory;
    }
}
