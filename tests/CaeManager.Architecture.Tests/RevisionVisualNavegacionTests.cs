using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Revisión visual de la PR 1413 contra el marco de Design (<c>marco-talveg.js</c> y «Revisión Interfaz sin Maqueta
/// TALVEG», §B, §C y N): fija en el CSS los valores literales que la revisión encontró distintos. Comprueba texto de
/// reglas, no estilo calculado (lo medido en navegador lo cubre quien abra la app). Lee el CSS dividiendo por bloques
/// <c>{ }</c>: no entiende anidación salvo <c>@media</c>/<c>@supports</c> (un nivel).
/// </summary>
public class RevisionVisualNavegacionTests
{
    private static readonly string Web = Path.Combine(Raiz(), "src", "CaeManager.Web");

    private static string Leer(params string[] ruta) => File.ReadAllText(Path.Combine([Web, .. ruta]));

    private static List<(string Selector, string Cuerpo)> Reglas(string css)
    {
        var sinComentarios = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Matches(sinComentarios, @"([^{}]+)\{([^{}]*)\}")
            .Select(m => (Regex.Replace(m.Groups[1].Value, @"\s+", " ").Trim(), m.Groups[2].Value))
            .ToList();
    }

    /// <summary>Los cuerpos de todas las reglas con exactamente ese selector (la base y sus ajustes de @media), juntos.</summary>
    private static string Cuerpo(string css, string selector)
    {
        var cuerpos = Reglas(css).Where(r => r.Selector == selector).Select(r => r.Cuerpo).ToList();
        cuerpos.Should().NotBeEmpty($"debe haber una regla «{selector}»");
        return string.Join("\n", cuerpos);
    }

    [Fact]
    public void El_menu_separa_los_grupos_12px_y_el_campo_de_busqueda_y_su_boton_miden_34px()
    {
        var css = Leer("Components", "Layout", "NavMenu.razor.css");

        Cuerpo(css, ".nav-principal").Should().Contain("gap: 0", "con un gap de 8 + el margin de 12 los grupos quedaban a 20 px");
        Cuerpo(css, ".nav-grupo-detalle").Should().Contain("margin-top: var(--space-3)");
        Cuerpo(css, ".nav-buscador").Should().Contain("height: 34px").And.Contain("padding: 0 10px").And.Contain("gap: 8px");
        Cuerpo(css, ".nav-compactar").Should().Contain("width: 34px").And.Contain("height: 34px");
        Cuerpo(css, ".nav-compactar").Should().NotContain("margin-left: auto");
        Cuerpo(css, ".nav-herramientas").Should().Contain("gap: 6px");
        css.Should().NotContain("data-abierta", "el campo está siempre visible: ya no hay estado abierto/recogido");
    }

    [Fact]
    public void La_barra_compacta_deja_el_avatar_de_la_empresa_centrado_y_el_boton_de_contraer()
    {
        var layout = Leer("Components", "Layout", "MainLayout.razor.css");
        Reglas(layout).Should().Contain(r => r.Selector.Contains(":not(.marca):not(nav):not(.selector-tenant)"),
            "la regla que oculta lo demás en la barra compacta no puede ocultar el selector de empresa");

        var selector = Leer("Components", "Layout", "SelectorClienteActivo.razor.css");
        // Con .barra-lateral: el selector del cajón móvil va completo aunque <html> lleve data-menu-compacto.
        Cuerpo(selector, "html[data-menu-compacto] .barra-lateral .selector-tenant-disparador").Should().Contain("justify-content: center").And.Contain("padding: 8px 0");
        Reglas(selector).Should().Contain(r => r.Selector.Contains("html[data-menu-compacto] .barra-lateral .selector-tenant-disparador .selector-tenant-texto")
                                               && r.Cuerpo.Contains("display: none"));
        Reglas(selector).Should().OnlyContain(r => !r.Selector.StartsWith("html[data-menu-compacto] .selector-tenant"),
            "ninguna regla compacta sin .barra-lateral: alcanzaría al cajón móvil");

        var menu = Leer("Components", "Layout", "NavMenu.razor.css");
        Cuerpo(menu, "html[data-menu-compacto] .barra-lateral .nav-herramientas").Should().Contain("justify-content: center");
        Reglas(menu).Should().Contain(r => r.Selector.StartsWith("html[data-menu-compacto] .barra-lateral .nav-buscador")
                                           && r.Cuerpo.Contains("display: none"),
            "con la barra compacta se oculta el campo y queda solo el botón de contraer");
    }

    [Fact]
    public void La_ventana_del_Gestor_CAE_lleva_16px_laterales_filas_de_40px_y_el_pie_de_la_ventana()
    {
        var css = Leer("Features", "Empresas", "Components", "CabeceraGestorCae.razor.css");

        Cuerpo(css, ".cabecera-gestor-cae-fila").Should().Contain("padding: 8px 16px").And.Contain("gap: 10px");
        Cuerpo(css, ".cabecera-gestor-cae-seccion").Should().Contain("padding: 0 16px 4px");
        Cuerpo(css, ".cabecera-gestor-cae-pie").Should().Contain("margin-top: var(--space-1)").And.Contain("padding: 12px 16px")
            .And.Contain("border-top: 1px solid var(--color-border)");
        Cuerpo(css, "strong.cabecera-gestor-cae-fila-nombre").Should().Contain("font-weight: 600");
        Cuerpo(css, ".cabecera-gestor-cae-principal").Should().Contain("border: 0", "la pastilla de «Principal» mide 18 px, sin borde");

        // Móvil: el rótulo baja bajo el nombre.
        css.Should().Contain("grid-template-columns: 32px minmax(0, 1fr)");
    }

    [Fact]
    public void Mi_avatar_trae_el_boton_de_texto_el_recorte_de_la_vista_previa_y_la_opacidad_de_deshabilitado()
    {
        var css = Leer("Features", "Usuarios", "Pages", "MiAvatar.razor.css");

        var iniciales = Reglas(css).Where(r => r.Selector.StartsWith(".mi-emblema ::deep .mi-emblema-iniciales,")).ToList();
        iniciales.Should().ContainSingle().Which.Cuerpo.Should().Contain("border: 0").And.Contain("background: transparent");
        Cuerpo(css, ".mi-emblema ::deep .mi-emblema-iniciales").Should().Contain("height: 36px").And.Contain("min-height: 36px",
            "Boton medio trae min-height: 40 y ganaría a la altura");
        Reglas(css).Should().Contain(r => r.Selector.Contains(".mi-emblema-guardar:disabled") && r.Cuerpo.Contains("opacity: 0.45"));

        var texto = Cuerpo(css, ".mi-emblema-contexto-texto");
        texto.Should().Contain("text-overflow: ellipsis").And.Contain("white-space: nowrap").And.Contain("min-width: 0");
        Cuerpo(css, ".mi-emblema-contexto-rotulo").Should().Contain("flex: none");
    }

    [Fact]
    public void La_tira_horizontal_de_Configuracion_empieza_a_1100px_como_en_el_marco()
    {
        var css = Leer("Features", "Configuracion", "Pages", "Configuracion.razor.css");

        css.Should().Contain("@media (max-width: 1100px)", "marco-talveg.js: @media (max-width:1100px) .tvm-cfg");
        css.Should().Contain("height: 52px");
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
