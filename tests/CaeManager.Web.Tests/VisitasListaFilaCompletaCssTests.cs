using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Maquetación que bUnit no calcula (el tbody de la lista de Visitas es un bloque y sus filas
/// de dato, una rejilla con huecos): se inspecciona la hoja de estilos fuente, como
/// <c>PulidoUxLoteFTests</c>. El ancho real y el fondo en hover se miden en el navegador.
/// </summary>
public class VisitasListaFilaCompletaCssTests
{
    private static string Css()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio");
        return File.ReadAllText(Path.Combine(dir!, "src", "CaeManager.Web", "Features", "Visitas", "Pages", "Visitas.razor.css"));
    }

    private static string Bloque(string css, string selectorExacto)
    {
        var m = Regex.Match(css, @"(?m)^" + Regex.Escape(selectorExacto) + @"\s*\{(?<c>[^}]*)\}");
        m.Success.Should().BeTrue($"debe existir la regla «{selectorExacto}»");
        return m.Groups["c"].Value;
    }

    [Fact]
    public void La_fila_desplegada_es_un_bloque_de_ancho_completo_y_lleva_el_fondo()
    {
        var bloque = Bloque(Css(), ".visitas-lista tr.visitas-fila-desplegada");
        bloque.Should().MatchRegex(@"display\s*:\s*block");
        bloque.Should().MatchRegex(@"width\s*:\s*100%");
        bloque.Should().Contain("background-color", "el fondo es de la fila, continuo hasta el borde derecho");
    }

    [Fact]
    public void El_hover_y_el_foco_los_pinta_la_fila_y_no_cada_celda()
    {
        var css = Css();
        Bloque(css, ".visitas-lista tr.fila-pulsable:hover").Should().Contain("var(--color-fila-hover)");
        Bloque(css, ".visitas-lista tr.fila-pulsable.fila-enfocada").Should().Contain("background-color");
        Regex.IsMatch(css, @"\.visitas-lista tr\.fila-pulsable:hover td[^{]*\{[^}]*background-color\s*:\s*transparent").Should().BeTrue(
            "las celdas no pintan el hover de .tabla-datos: con el gap de la rejilla dejaría franjas sin fondo");
        Regex.IsMatch(css, @"\.visitas-lista tr\.fila-pulsable\.fila-enfocada td\s*\{[^}]*box-shadow\s*:\s*none").Should().BeTrue(
            "la barra de foco de j/k la lleva la fila: por celda se repetiría una barra en cada columna");
    }
}
