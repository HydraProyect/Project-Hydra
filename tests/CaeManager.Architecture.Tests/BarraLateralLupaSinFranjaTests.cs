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
        razor.Should().Contain("data-menu-filtro", "control positivo: el campo «Buscar en el menú» está");
        razor.Should().NotContain("<button type=\"button\" class=\"nav-lupa\"", "el campo está siempre visible: ya no hay lupa que lo despliegue");
        js.Should().Contain("Escape", "Esc sigue vaciando el campo");
        Css.Should().Contain("height: 34px", "el campo y el botón de compactar miden 34 px (.tvm-buscam, marco de Design)");
    }

    /// <summary>
    /// Revisión visual de la PR 1413 (marco-talveg.js, <c>.tvm-logo</c> y <c>.tvm-cae</c>): la marca mide los 64 px de la
    /// cabecera SIN encoger (<c>flex: none</c>), con padding 0 18 y gap 10, y la etiqueta «CAE» mide 21 px
    /// (<c>line-height: 1</c> + padding 4 + borde 1).
    /// </summary>
    [Fact]
    public void La_marca_mide_64px_sin_encoger_y_la_etiqueta_CAE_21px()
    {
        var reglas = Reglas(Leer("Components", "Layout", "MainLayout.razor.css"));

        var marca = reglas.Where(r => r.Selector == ".marca").Should().ContainSingle().Which.Cuerpo;
        marca.Should().Contain("height: var(--navbar-height)").And.Contain("flex: none")
            .And.Contain("padding: 0 18px").And.Contain("gap: 10px");

        var etiqueta = reglas.Where(r => r.Selector == ".marca-etiqueta").Should().ContainSingle().Which.Cuerpo;
        etiqueta.Should().Contain("/1 ").And.Contain("padding: 4px 6px").And.Contain("border: 1px solid var(--color-border-strong)");

        Leer("wwwroot", "css", "tokens.css").Should().Contain("--navbar-height: 64px", "la regla de la marca iguala la cabecera de 64 px");
    }

    [Fact]
    public void El_elemento_seleccionado_no_lleva_franja_lateral()
    {
        var activas = Reglas(Css).Where(r => Regex.IsMatch(r.Selector, @"\.nav-item\.active\b")).ToList();
        activas.Should().NotBeEmpty("control positivo: el instrumento encuentra las reglas de la selección");
        activas.Should().Contain(r => r.Cuerpo.Contains("font-weight: 600"), "peso, color y fondo la distinguen");

        activas.Where(r => r.Selector.EndsWith(".nav-item.active:hover")).Should().ContainSingle()
            .Which.Cuerpo.Should().Contain("background-color: var(--color-primary-50)",
                "el seleccionado conserva su fondo al pasar el cursor (marco de Design, DDL-083)");
        Reglas(Css).Where(r => r.Selector.EndsWith(".nav-item")).Should().OnlyContain(
            r => !r.Cuerpo.Contains("border:") && !r.Cuerpo.Contains("border-color"),
            "los enlaces del menú no llevan marco; el hover solo cambia el fondo (marco de Design, DDL-083)");

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
    public void La_fila_activa_del_selector_de_Tenant_no_lleva_franja_lateral()
    {
        var activas = Reglas(Leer("Components", "Layout", "SelectorClienteActivo.razor.css"))
            .Where(r => r.Selector.StartsWith(".selector-tenant-fila-activa")).ToList();

        activas.Should().NotBeEmpty("control positivo: el instrumento encuentra la regla de la fila activa");
        activas.Should().Contain(r => r.Cuerpo.Contains("background"));
        activas.Should().OnlyContain(r => !r.Cuerpo.Contains("box-shadow") && !r.Cuerpo.Contains("inset")
            && !r.Cuerpo.Contains("border-left"), "la selección no lleva franja");
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
        var tramo = css[posMedia..posAnim];
        (tramo.Count(c => c == '{') - tramo.Count(c => c == '}')).Should().BeGreaterThan(0,
            "la animación debe estar DENTRO del @media, no tras cerrarlo");
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
        // Usuarios y Roles salieron del hub (sección Equipo, 2026-10-17): quedan diez que el patrón reconoce.
        configuradas.Should().HaveCountGreaterThanOrEqualTo(10, "control positivo: el patrón encuentra las entradas");

        foreach (var (id, clave) in configuradas)
        {
            hub.Should().Contain($"new(\"{id}\", ", $"el hub debe tener la entrada '{id}'");
            hub.Should().Contain($"Textos[\"{clave}\"]", $"el hub rotula '{id}' con {clave}");
        }

        // Las entradas con autoridad propia no se ofrecen: no son del rol Administrador del hub.
        // «Administración de plataforma» sí es un ENLACE del menú (solo con capacidad de plataforma), pero no una subopción de la lupa.
        var subopciones = catalogo[catalogo.IndexOf("Subopciones { get; }", StringComparison.Ordinal)..];
        subopciones.Should().NotContain("configuracion/plataforma").And.NotContain("configuracion/orden-menu")
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
