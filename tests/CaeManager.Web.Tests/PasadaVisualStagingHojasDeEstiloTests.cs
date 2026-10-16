using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Pasada visual de staging (2026-10-17). bUnit no calcula estilos, así que lo que se congela aquí son las reglas de las
/// hojas que arreglan cada defecto, con un control positivo por regla para que el test no pase por no mirar nada.
/// </summary>
public class PasadaVisualStagingHojasDeEstiloTests
{
    /// <summary>
    /// <c>.barra-filtros { display: flex }</c> anulaba el <c>hidden</c> de Centro 360 (la regla del navegador es de agente de
    /// usuario y pierde contra cualquier <c>display</c> de autor): el buscador «Buscar trabajador en este centro…» salía en
    /// todas las pestañas aunque el marcado lo ocultara (esa parte ya la cubren los tests de Centro360Gen2).
    /// </summary>
    [Fact]
    public void La_barra_de_filtros_oculta_con_hidden_no_se_vuelve_a_pintar()
    {
        var css = Leer("wwwroot", "css", "list-page.css");

        css.Should().MatchRegex(@"(?m)^\.barra-filtros\s*\{[^}]*display\s*:\s*flex", "control positivo: la regla que anulaba hidden existe");
        var regla = Regex.Match(css, @"(?m)^\.barra-filtros\[hidden\]\s*\{(?<cuerpo>[^}]*)\}");
        regla.Success.Should().BeTrue("sin esta regla, display:flex gana a hidden");
        regla.Groups["cuerpo"].Value.Should().MatchRegex(@"display\s*:\s*none");
    }

    /// <summary>Ningún otro uso de <c>.barra-filtros</c> en marcado se oculta con <c>hidden</c> sin que la regla acotada lo cubra.</summary>
    [Fact]
    public void Solo_Centro_360_oculta_con_hidden_una_barra_filtros_y_la_regla_lo_cubre()
    {
        var usos = Directory.EnumerateFiles(Ruta("Features"), "*.razor", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Ruta("Components"), "*.razor", SearchOption.AllDirectories))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"<[a-z]+(?=[^>]*\sclass=""(?:[^""]*\s)?barra-filtros(?:\s[^""]*)?"")(?=[^>]*\shidden=)"))
            .Select(Path.GetFileName)
            .ToList();

        usos.Should().Equal(["CentroDetalle.razor"]);
    }

    /// <summary>Historial: cada evento es una fila de una tarjeta, con la fecha alineada a la derecha.</summary>
    [Fact]
    public void El_historial_es_una_tarjeta_de_filas_con_la_fecha_a_la_derecha()
    {
        var css = Leer("Components", "Workspace", "PestanaHistorial.razor.css");

        Cuerpo(css, ".workspace-historial-lista").Should().Contain("border:").And.Contain("border-radius:").And.Contain("background:");
        Cuerpo(css, ".workspace-historial-item").Should().Contain("display: grid");
        Cuerpo(css, ".workspace-historial-fecha").Should().Contain("text-align: right");
    }

    /// <summary>Agenda del empleador: un correo largo no tiene dónde partir y desbordaba la tarjeta.</summary>
    [Fact]
    public void El_correo_de_la_agenda_se_ajusta_dentro_de_la_tarjeta()
    {
        var css = Leer("Components", "Workspace", "PestanaAgendaContactos.razor.css");

        css.Should().MatchRegex(@"(?s)\.agenda-contacto-email[^{]*\{[^}]*overflow-wrap\s*:\s*anywhere");
        Cuerpo(css, ".agenda-contacto-datos").Should().Contain("min-width: 0");
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
