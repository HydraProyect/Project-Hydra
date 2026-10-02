using FluentAssertions;
using static CaeManager.Architecture.Tests.ContrasteCss;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Tres restos de <c>--color-primary-100</c> (#dcebff) sin variante oscura, los
/// que dejó anotados la PR #1037 (avatar del Tenant en modo oscuro): el texto
/// seleccionado, el elemento activo del menú lateral bajo el puntero y el fondo
/// detrás de los logos de marca. Medidos el 2026-10-02 en el árbol real.
///
/// <list type="bullet">
/// <item><b>Selección.</b> <c>::selection</c> solo fijaba el fondo
/// (<c>--color-primary-100</c>) y dejaba la letra al elemento: en oscuro,
/// #e7eaee sobre #dcebff (1,00:1, texto ilegible).</item>
/// <item><b>Menú lateral.</b> <c>.nav-item.active:hover</c> pintaba
/// <c>--color-primary-100</c> de fondo con la letra en <c>--color-primary-500</c>,
/// que en oscuro se remapea a #5ca2f4: 2,19:1.</item>
/// <item><b>Logo.</b> El fondo era <c>--color-surface</c>; el mockup pinta #fff
/// fijo y, sobre el fondo oscuro de la superficie, un logo de tinta oscura sobre
/// transparencia se pierde. Aquí no hay letras que medir: se exige que el fondo
/// resuelva a blanco en los dos temas.</item>
/// <item><b>Botón de avisos normativos</b> (hallado al buscar el mismo patrón en
/// el árbol): icono <c>--color-primary-500</c> (#5ca2f4 en oscuro) sobre
/// <c>--color-neutral-0</c> (#fff fijo): 2,65:1.</item>
/// </list>
///
/// <para>
/// <b>Contrato efectivo.</b> Para cada regla de la lista lee del <c>.css</c> real
/// el <c>background</c> y el <c>color</c> declarados (fusionando la base con su
/// variante), resuelve los <c>var()</c> contra <c>tokens.css</c> con el bloque
/// del tema por delante de <c>:root</c>, y exige &gt;= 4,5:1 (WCAG AA) en
/// oscuro y en claro. «Sistema» resuelve a los valores de <c>:root</c> (los de
/// claro) porque <c>prefers-color-scheme</c> está desactivado en
/// <c>tokens.css</c>.
/// </para>
///
/// <para>
/// <b>Límite declarado.</b> Mide las reglas de la lista, no todo el árbol: otro
/// par fondo/letra con el mismo patrón no entra solo aquí (sí lo caza el barrido de
/// <see cref="ContrasteDeComponentesPorTemaTests"/>, para las reglas que declaran
/// fondo y letra a la vez; un fondo de logo, sin letra, solo se mide aquí). Lee la primera regla cuyo
/// selector es exactamente el indicado; un override por estado o contexto no
/// listado no se mide. Solo entiende <c>#rrggbb</c> y <c>var()</c>. Y el fondo
/// de logo se mide por resolución del token, no contra el color pintado: el
/// color pintado lo mira <c>SelectorTemaTests</c> (E2E) en el navegador.
/// </para>
/// </summary>
public class ContrasteDeSeleccionHoverYLogoPorTemaTests
{
    private const double UmbralAa = 4.5;
    private const string Blanco = "#ffffff";

    private sealed record ParDeColores(string Nombre, string Fichero, string[] Selectores);

    private static readonly ParDeColores[] Pares =
    [
        new("Texto seleccionado (::selection)", "wwwroot/css/base.css", ["::selection"]),
        new("Elemento activo del menú lateral bajo el puntero", "Components/Layout/NavMenu.razor.css",
            [".nav-principal ::deep .nav-item.active", ".nav-principal ::deep .nav-item.active:hover"]),
        new("Icono del botón flotante de avisos normativos",
            "Features/VigilanciaNormativa/PanelAvisosNormativos.razor.css", [".boton-avisos-normativos"]),
    ];

    private static readonly ParDeColores[] Logos =
    [
        new("Logo en AvatarTenant", "Components/DesignSystem/AvatarTenant.razor.css", [".avatar-tenant-logo"]),
        new("Logo de la organización", "Features/Configuracion/Pages/OrganizacionLogo.razor.css",
            [".organizacion-logo-imagen"]),
        // 15 de las 20 marcas de plataforma CAE son imágenes con transparencia y en 7 la tinta es
        // mayoritariamente oscura (CTAIMA, 100 %): sobre --color-surface en oscuro se pierden.
        new("Logo de plataforma CAE en Inicio", "Features/Dashboard/Components/FilaPlataforma.razor.css",
            [".fila-plataforma-logo"]),
    ];

    [Theory]
    [InlineData("oscuro")]
    [InlineData("claro")]
    public void El_texto_de_cada_par_cumple_AA_en_el_tema(string tema)
    {
        var tokens = Tokens.Desde(File.ReadAllText(RutaTokensCss()));
        tokens.Completo.Should().BeTrue("tokens.css debe declarar :root y los bloques de tema oscuro y claro");

        var fallos = new List<string>();
        foreach (var par in Pares)
        {
            var (fondo, texto) = ColoresDeLaRegla(Leer(par), par.Selectores);
            fondo.Should().NotBeNull($"{par.Nombre}: sin background propio no se puede medir el contraste");
            texto.Should().NotBeNull($"{par.Nombre}: sin color propio hereda un color que este test no ve");

            var ratio = Contraste(tokens.Resolver(fondo!, tema), tokens.Resolver(texto!, tema));
            if (ratio < UmbralAa)
                fallos.Add($"{par.Nombre} ({par.Fichero}): {ratio:0.00}:1 en {tema} [fondo {fondo} / letra {texto}]");
        }

        string.Join("\n", fallos).Should().BeEmpty(
            "el texto (o icono) de cada par tiene que leerse (>= 4,5:1) en los dos temas; si fondo y letra salen de " +
            "tokens que no cambian de tema a la vez, usa un token con variante en tokens.css " +
            "(--color-seleccion-*, --color-nav-activo-hover-fondo)");
    }

    [Theory]
    [InlineData("oscuro")]
    [InlineData("claro")]
    public void El_fondo_del_logo_es_blanco_de_marca_en_el_tema_y_sale_de_un_token(string tema)
    {
        var tokens = Tokens.Desde(File.ReadAllText(RutaTokensCss()));
        tokens.Completo.Should().BeTrue("tokens.css debe declarar :root y los bloques de tema oscuro y claro");

        foreach (var logo in Logos)
        {
            var (fondo, _) = ColoresDeLaRegla(Leer(logo), logo.Selectores);

            fondo.Should().Be("var(--color-logo-fondo)",
                $"{logo.Nombre}: el fondo del logo va como token, no como literal ni como superficie");
            tokens.Resolver(fondo!, tema).Should().Be(Blanco,
                $"{logo.Nombre}: el mockup pinta el logo sobre #fff en {tema}; sobre --color-surface un logo de " +
                "tinta oscura sobre transparencia se pierde en oscuro");
        }
    }

    // ---- Sensibilidad: el instrumento tiene que ver los defectos del 2026-10-02 ----

    private const string TokensDelDefecto = """
        :root {
          --color-primary-100: #dcebff;
          --color-primary-300: #5ca2f4;
          --color-primary-500: #235bc2;
          --color-text: #161e27;
          --color-bg: #f6f8fa;
          --color-surface: #ffffff;
          --color-logo-fondo: #ffffff;
        }
        :root[data-theme='oscuro'] {
          --color-primary-500: var(--color-primary-300);
          --color-text: #e7eaee;
          --color-bg: #0e141b;
          --color-surface: #17212c;
          --color-surface-hover: #202b36;
        }
        :root[data-theme='claro'] {
          --color-text: #161e27;
        }
        """;

    [Fact]
    public void El_instrumento_marca_la_seleccion_del_defecto_en_oscuro_pero_no_en_claro()
    {
        var tokens = Tokens.Desde(TokensDelDefecto);
        var (fondo, texto) = ColoresDeLaRegla(
            "::selection { background-color: var(--color-primary-100); color: var(--color-text); }", ["::selection"]);

        Contraste(tokens.Resolver(fondo!, "oscuro"), tokens.Resolver(texto!, "oscuro"))
            .Should().BeLessThan(1.5, "es el defecto: #e7eaee sobre #dcebff");
        Contraste(tokens.Resolver(fondo!, "claro"), tokens.Resolver(texto!, "claro"))
            .Should().BeGreaterThan(UmbralAa, "control negativo: en claro el mismo par se lee");
    }

    [Fact]
    public void El_instrumento_marca_el_hover_del_activo_del_defecto_en_oscuro_pero_no_en_claro()
    {
        var tokens = Tokens.Desde(TokensDelDefecto);
        const string css = """
            .activo { background-color: var(--color-primary-50); color: var(--color-primary-500); }
            .activo:hover { background-color: var(--color-primary-100); }
            """;
        var (fondo, texto) = ColoresDeLaRegla(css, [".activo", ".activo:hover"]);

        fondo.Should().Be("var(--color-primary-100)", "el hover sobrescribe el fondo de la base");
        Contraste(tokens.Resolver(fondo!, "oscuro"), tokens.Resolver(texto!, "oscuro"))
            .Should().BeLessThan(2.3, "es el defecto: #5ca2f4 sobre #dcebff (2,19:1)");
        Contraste(tokens.Resolver(fondo!, "claro"), tokens.Resolver(texto!, "claro"))
            .Should().BeGreaterThan(UmbralAa, "control negativo: en claro el mismo par se lee");
    }

    [Fact]
    public void El_instrumento_sigue_los_tokens_corregidos_por_tema()
    {
        const string corregido = """
            :root {
              --color-primary-100: #dcebff;
              --color-primary-300: #5ca2f4;
              --color-primary-500: #235bc2;
              --color-text: #161e27;
              --color-bg: #f6f8fa;
              --color-surface-hover: #f4f6f8;
              --color-seleccion-fondo: var(--color-primary-100);
              --color-seleccion-texto: var(--color-text);
              --color-nav-activo-hover-fondo: var(--color-primary-100);
            }
            :root[data-theme='oscuro'] {
              --color-primary-500: var(--color-primary-300);
              --color-text: #e7eaee;
              --color-bg: #0e141b;
              --color-surface-hover: #202b36;
              --color-seleccion-fondo: var(--color-primary-300);
              --color-seleccion-texto: var(--color-bg);
              --color-nav-activo-hover-fondo: var(--color-surface-hover);
            }
            :root[data-theme='claro'] {
              --color-text: #161e27;
            }
            """;
        var tokens = Tokens.Desde(corregido);
        var (fondoSel, textoSel) = ColoresDeLaRegla(
            "::selection { background-color: var(--color-seleccion-fondo); color: var(--color-seleccion-texto); }",
            ["::selection"]);
        var (fondoNav, textoNav) = ColoresDeLaRegla(
            ".a { color: var(--color-primary-500); } .a:hover { background-color: var(--color-nav-activo-hover-fondo); }",
            [".a", ".a:hover"]);

        foreach (var tema in new[] { "oscuro", "claro" })
        {
            Contraste(tokens.Resolver(fondoSel!, tema), tokens.Resolver(textoSel!, tema)).Should().BeGreaterThan(UmbralAa, $"selección en {tema}");
            Contraste(tokens.Resolver(fondoNav!, tema), tokens.Resolver(textoNav!, tema)).Should().BeGreaterThan(UmbralAa, $"hover en {tema}");
        }
    }

    [Fact]
    public void El_instrumento_distingue_un_fondo_de_logo_sobre_superficie_de_uno_blanco_fijo()
    {
        var tokens = Tokens.Desde(TokensDelDefecto);
        var (sobreSuperficie, _) = ColoresDeLaRegla(".logo { background: var(--color-surface); }", [".logo"]);
        var (blancoFijo, _) = ColoresDeLaRegla(".logo { background: var(--color-logo-fondo); }", [".logo"]);

        tokens.Resolver(sobreSuperficie!, "oscuro").Should().NotBe(Blanco, "es el defecto: la superficie oscura");
        tokens.Resolver(sobreSuperficie!, "claro").Should().Be(Blanco, "control negativo: en claro la superficie ya es blanca");
        tokens.Resolver(blancoFijo!, "oscuro").Should().Be(Blanco);
        tokens.Resolver(blancoFijo!, "claro").Should().Be(Blanco);
    }

    private static string Leer(ParDeColores par)
    {
        var ruta = Path.Combine(RaizWeb(), par.Fichero.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(ruta).Should().BeTrue($"{par.Fichero} debe existir (lista de reglas desfasada)");
        return File.ReadAllText(ruta);
    }
}
