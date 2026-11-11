using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Contrato del CSS de las cabeceras de sección del drawer de Centro (demo 2026-11-10). bUnit no aplica CSS: este test lee el fichero
/// y fija la regla que evita que el título se vaya a la derecha y se monte sobre su botón. No comprueba cómo la pinta un navegador.
/// </summary>
public class WorkspaceCabeceraSeccionCssTests
{
    private static string Css()
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, "CaeManager.slnx")))
            directorio = directorio.Parent;

        directorio.Should().NotBeNull("hay que encontrar la raíz del repositorio para leer el CSS");
        return File.ReadAllText(Path.Combine(directorio!.FullName, "src", "CaeManager.Web", "wwwroot", "css", "workspace.css"));
    }

    /// <summary>El cuerpo de la primera regla cuyo selector es exactamente <paramref name="selector"/>.</summary>
    private static string Regla(string css, string selector)
    {
        var inicio = css.IndexOf(selector + " {", StringComparison.Ordinal);
        inicio.Should().BeGreaterThanOrEqualTo(0, $"debe existir la regla «{selector}»");
        var fin = css.IndexOf('}', inicio);
        return css[(inicio + selector.Length)..fin];
    }

    [Fact]
    public void La_cabecera_de_seccion_pone_el_titulo_a_la_izquierda_y_la_accion_baja_de_linea_en_panel_estrecho()
    {
        var css = Css();

        var cabecera = Regla(css, ".workspace-cabecera-seccion");
        cabecera.Should().Contain("justify-content: space-between").And.Contain("flex-wrap: wrap").And.Contain("gap:");
        cabecera.Should().NotContain("flex-end");

        Regla(css, ".workspace-cabecera-seccion > h3").Should().Contain("flex: 1 1").And.Contain("min-width: 0");
    }
}
