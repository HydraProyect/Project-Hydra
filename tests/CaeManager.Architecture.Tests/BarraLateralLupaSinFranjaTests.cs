using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Ajustes de la barra lateral del 2026-10-08 sobre la PR 1109, fijados donde se pueden medir sin
/// navegador (lo visual lo cubre quien abra la app, no este test):
/// <list type="number">
/// <item>El filtro es solo una lupa; no queda atajo «/» ni insignia <c>kbd</c>.</item>
/// <item>El elemento seleccionado (<c>.nav-item.active</c>) se distingue por fondo, color y peso, sin
/// franja (<c>border-left</c>, <c>inset</c> en <c>box-shadow</c>, pseudoelemento).</item>
/// <item>El icono se anima solo al pasar el cursor o al enfocar, con un único keyframe, y nunca al
/// abrir un grupo ni con <c>prefers-reduced-motion</c>.</item>
/// <item>La barra de scroll de la barra lateral usa los estándares con tokens.</item>
/// <item>Las subopciones de Configuración que ofrece la lupa existen en el hub (ids y claves de rótulo).</item>
/// </list>
/// Límite declarado: lee el CSS con una división simple por bloques <c>{ }</c>; no entiende anidación
/// salvo <c>@media</c>/<c>@supports</c> (un nivel) y comprueba texto, no el estilo calculado.
/// </summary>
public class BarraLateralLupaSinFranjaTests
{
    private static readonly string Web = Path.Combine(Raiz(), "src", "CaeManager.Web");

    private static string Leer(params string[] ruta) =>
        File.ReadAllText(Path.Combine([Web, .. ruta]));

    private static string Css => Leer("Components", "Layout", "NavMenu.razor.css");

    /// <summary>Reglas planas (selector, cuerpo) a cualquier profundidad de @media/@supports.</summary>
    private static List<(string Selector, string Cuerpo)> Reglas(string css)
    {
        var sinComentarios = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Matches(sinComentarios, @"([^{}]+)\{([^{}]*)\}")
            .Select(m => (Regex.Replace(m.Groups[1].Value, @"\s+", " ").Trim(), m.Groups[2].Value))
            .ToList();
    }

    [Fact]
    public void No_queda_atajo_de_teclado_ni_insignia_en_la_barra()
    {
        var js = Leer("wwwroot", "js", "menu-lateral.js");
        var razor = Leer("Components", "Layout", "NavMenu.razor");

        js.Should().NotContain("evento.key !== '/'", "el atajo «/» se retiró");
        Regex.IsMatch(js, @"key\s*[=!]==?\s*'/'").Should().BeFalse();
        razor.Should().NotContain("<kbd");
        razor.Should().Contain("data-menu-lupa", "control positivo: la lupa sí está");
        js.Should().Contain("Escape", "Esc sigue cerrando el campo");
    }

    [Fact]
    public void El_elemento_seleccionado_no_lleva_franja_lateral()
    {
        var activas = Reglas(Css).Where(r => Regex.IsMatch(r.Selector, @"\.nav-item\.active\b")).ToList();
        activas.Should().NotBeEmpty("control positivo: el instrumento encuentra las reglas de la selección");
        activas.Should().Contain(r => r.Cuerpo.Contains("font-weight: 600"), "peso, color y fondo la distinguen");

        foreach (var (selector, cuerpo) in activas)
        {
            cuerpo.Should().NotContainAny(["border-left", "border-inline-start", "inset", "box-shadow", "outline"],
                $"'{selector}': la selección no lleva franja ni marco lateral");
            selector.Should().NotContain("::before").And.NotContain("::after");
        }

        Reglas(Css).Where(r => r.Selector.Contains("nav-item::before") || r.Selector.Contains("nav-item::after"))
            .Should().BeEmpty("tampoco se simula la franja con un pseudoelemento del enlace");
    }

    [Fact]
    public void El_icono_se_anima_solo_con_hover_o_foco_y_sin_entrada_escalonada()
    {
        var css = Css;
        css.Should().NotContain("nav-grupo-item-entra", "la entrada escalonada al abrir el grupo se retiró");

        var conAnimacion = Reglas(css)
            .Where(r => Regex.IsMatch(r.Cuerpo, @"animation\s*:(?>\s*)(?!none)")).ToList();
        conAnimacion.Should().ContainSingle("un único keyframe en la barra");
        conAnimacion[0].Selector.Should().Contain(":hover").And.Contain(":focus-visible");
        conAnimacion[0].Cuerpo.Should().Contain("nav-icono-sacude");
        Regex.Matches(css, @"@keyframes\s+[\w-]+").Should().ContainSingle();

        // Envuelta en no-preference: con prefers-reduced-motion no se mueve.
        var posAnim = css.IndexOf("animation: nav-icono-sacude", StringComparison.Ordinal);
        var posMedia = css.LastIndexOf("@media (prefers-reduced-motion: no-preference)", posAnim, StringComparison.Ordinal);
        posMedia.Should().BeGreaterThan(-1);
    }

    [Fact]
    public void La_barra_de_scroll_de_la_barra_lateral_usa_estandares_y_tokens()
    {
        var reglas = Reglas(Css);
        var propia = reglas.Where(r => r.Selector == ".nav-principal" && r.Cuerpo.Contains("scrollbar-width: thin")).ToList();
        propia.Should().ContainSingle();
        propia[0].Cuerpo.Should().Contain("scrollbar-color").And.Contain("var(--color-text-muted)");

        reglas.Should().Contain(r => r.Selector == ".nav-principal::-webkit-scrollbar-thumb"
                                     && r.Cuerpo.Contains("var(--radius-full)"),
            "Safari y Chromium antiguo: pulgar redondeado");

        Leer("wwwroot", "css", "base.css").Should().NotContain(".nav-principal",
            "un único sitio estiliza esa barra (NavMenu.razor.css)");
    }

    /// <summary>
    /// El catálogo duplica los ids y las claves de rótulo del hub (su lista es privada y de instancia);
    /// si alguien renombra una entrada del hub, la lupa apuntaría a una ruta que ya no resuelve.
    /// </summary>
    [Fact]
    public void Las_subopciones_de_Configuracion_existen_en_el_hub_con_su_clave_de_rotulo()
    {
        var hub = Leer("Features", "Configuracion", "Pages", "Configuracion.razor.cs");
        var catalogo = Leer("Components", "Layout", "CatalogoMenuLateral.cs");

        var configuradas = Regex.Matches(catalogo,
                @"new\(""config-[\w-]+"", ""configuracion"", ""configuracion/([\w-]+)"", ""(\w+)"", FuenteRotuloSubopcion\.TextosConfiguracion\)")
            .Select(m => (Id: m.Groups[1].Value, Clave: m.Groups[2].Value)).ToList();
        configuradas.Should().HaveCountGreaterThan(10, "control positivo: el patrón encuentra las entradas");

        foreach (var (id, clave) in configuradas)
        {
            hub.Should().Contain($"new(\"{id}\", ", $"el hub debe tener la entrada '{id}'");
            hub.Should().Contain($"Textos[\"{clave}\"]", $"el hub rotula '{id}' con {clave}");
        }

        // Las entradas con autoridad propia no se ofrecen: no son del rol Administrador del hub.
        catalogo.Should().NotContain("configuracion/plataforma").And.NotContain("configuracion/orden-menu")
            .And.NotContain("configuracion/organizacion").And.NotContain("configuracion/accesos-sensibles");
    }

    private static string Raiz()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);
        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;
        return actual?.FullName ?? throw new InvalidOperationException(
            "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);
    }
}
