using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Revisión 1:1 del Marco (cabecera) frente al export de Design (<c>marco-talveg.js</c>, 2026-11-08). bUnit no calcula
/// estilos, así que se congelan las reglas de las hojas que corrigen cada divergencia medida, con control positivo.
/// </summary>
public class RevisionUnoAUnoMarcoHojasDeEstiloTests
{
    /// <summary>La miga de la página actual mide 14 px peso 500 (<c>.tvm-migas .act</c>), no 15/600.</summary>
    [Fact]
    public void La_miga_actual_es_de_14_px_y_peso_500()
    {
        var cuerpo = Cuerpo(Leer("Components", "Layout", "MigasPagina.razor.css"), ".miga-actual");

        cuerpo.Should().MatchRegex(@"font\s*:\s*500\s+0?\.875rem");
        cuerpo.Should().NotContain("0.9375rem");
    }

    /// <summary>El botón Buscar es de fondo surface y radio md (<c>.tvm-busca</c>); conserva el borde de control (3:1).</summary>
    [Fact]
    public void El_boton_buscar_tiene_fondo_surface_y_radio_md_con_borde_de_control()
    {
        var cuerpo = Cuerpo(Leer("Features", "BusquedaGlobal", "BotonBuscadorGlobal.razor.css"), ".boton-buscador-global");

        cuerpo.Should().Contain("background-color: var(--color-surface);");
        cuerpo.Should().Contain("border-radius: var(--radius-md)");
        cuerpo.Should().Contain("border: var(--border-control)");
    }

    /// <summary>El atajo Ctrl+K es 11/14 peso 600, relleno 2/6, radio 4, borde fuerte y fondo sutil (<c>.tvm-busca kbd</c>).</summary>
    [Fact]
    public void El_atajo_de_teclado_sigue_el_kbd_del_marco()
    {
        var cuerpo = Cuerpo(Leer("Features", "BusquedaGlobal", "BotonBuscadorGlobal.razor.css"), ".atajo-teclado");

        cuerpo.Should().MatchRegex(@"font\s*:\s*600\s+0?\.6875rem/0?\.875rem");
        cuerpo.Should().Contain("padding: 2px 6px");
        cuerpo.Should().Contain("border-radius: 4px");
        cuerpo.Should().Contain("border: 1px solid var(--color-border-strong)");
        cuerpo.Should().Contain("background-color: var(--color-surface-subtle)");
    }

    /// <summary>Las iniciales del avatar de la cabecera son de 12 px peso 700 (<c>.tvm-yo</c>).</summary>
    [Fact]
    public void El_avatar_de_la_cabecera_usa_iniciales_de_12_px_y_peso_700()
    {
        var css = Leer("Components", "Layout", "MenuUsuario.razor.css");

        var m = Regex.Match(css, @"(?m)^\.menu-cuenta-boton ::deep \.avatar-usuario\s*\{(?<cuerpo>[^}]*)\}");
        m.Success.Should().BeTrue("control positivo: existe la regla del avatar del botón de cuenta");
        m.Groups["cuerpo"].Value.Should().Contain("font-size: 0.75rem").And.Contain("font-weight: 700");
    }

    /// <summary>
    /// Agenda del empleador a 375 px: el botón «Añadir contacto» no se encoge (flex) y recortaba su texto por su
    /// <c>overflow:hidden</c>; cede el resumen.
    /// </summary>
    [Fact]
    public void El_boton_de_anadir_contacto_de_la_agenda_no_se_encoge()
    {
        var css = Leer("Components", "Workspace", "PestanaAgendaContactos.razor.css");

        css.Should().MatchRegex(@"(?s)\.agenda-encabezado ::deep \.boton\s*\{[^}]*flex\s*:\s*0\s+0\s+auto");
        Cuerpo(css, ".agenda-resumen").Should().Contain("min-width: 0");
    }

    private static string Cuerpo(string css, string selector)
    {
        var m = Regex.Match(css, @"(?m)^" + Regex.Escape(selector) + @"\s*\{(?<cuerpo>[^}]*)\}");
        m.Success.Should().BeTrue($"control positivo: existe la regla {selector}");
        return m.Groups["cuerpo"].Value;
    }

    private static string Leer(params string[] partes) => File.ReadAllText(Ruta(partes));

    private static string Ruta(params string[] partes)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio para leer las hojas de estilo");
        return Path.Combine([dir!, "src", "CaeManager.Web", .. partes]);
    }
}
