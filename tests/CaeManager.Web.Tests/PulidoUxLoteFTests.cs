using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Lote F del recorrido en vivo del 2026-10-01 (D-28, D-30): propiedades de
/// maquetación y de marcado que bUnit no calcula. Se inspeccionan los ficheros
/// fuente, como <c>ComunicacionesHojaDeEstilosTests</c>.
/// </summary>
public class PulidoUxLoteFTests
{
    private static string Leer(string relativa)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio");
        return File.ReadAllText(Path.Combine(dir!, "src", "CaeManager.Web", relativa));
    }

    [Fact]
    public void El_boton_medio_mide_40_px_como_el_mockup_y_44_con_puntero_tactil()
    {
        var css = Leer("Components/DesignSystem/Boton.razor.css");

        var medio = Regex.Match(css, @"(?m)^\.boton-medio\s*\{(?<c>[^}]*)\}");
        medio.Success.Should().BeTrue();
        medio.Groups["c"].Value.Should().MatchRegex(@"min-height\s*:\s*40px").And.NotContain("14px").And.NotContain("0.875rem");

        var tactil = Regex.Match(css, @"@media \(max-width: 767px\), \(pointer: coarse\)\s*\{(?<c>.*?)\n\}", RegexOptions.Singleline);
        tactil.Groups["c"].Value.Should().MatchRegex(@"\.boton-medio\s*\{[^}]*min-height\s*:\s*44px", "el objetivo táctil de WCAG 2.5.5 sigue en 44 px");
    }

    [Fact]
    public void El_layout_ofrece_saltar_al_contenido_y_el_destino_recibe_el_foco()
    {
        var layout = Leer("Components/Layout/MainLayout.razor");

        layout.Should().MatchRegex(@"<a class=""saltar-al-contenido"" href=""#contenido-principal"">@TextosComunes\[""SaltarAlContenido""\]</a>");
        layout.Should().MatchRegex(@"<main class=""contenido"" id=""contenido-principal"" tabindex=""-1"">");
        layout.IndexOf("saltar-al-contenido", StringComparison.Ordinal).Should().BeLessThan(layout.IndexOf("barra-lateral", StringComparison.Ordinal),
            "el enlace tiene que ser lo primero que alcanza el Tab, antes del menú");
    }

    [Fact]
    public void Asignar_empresas_lista_a_altura_natural_y_su_salida_se_llama_Cancelar()
    {
        Leer("Features/Usuarios/Pages/Usuarios.razor").Should().Contain("lista-seleccion-multiple lista-seleccion-multiple-natural");
        Regex.Match(Leer("wwwroot/css/list-page.css"), @"\.lista-seleccion-multiple\.lista-seleccion-multiple-natural\s*\{(?<c>[^}]*)\}").Groups["c"].Value
            .Should().MatchRegex(@"max-height\s*:\s*none");
        Regex.IsMatch(Leer("Features/Usuarios/Recursos/TextosUsuarios.resx"), @"AsignarEmpresasVolver""[^>]*>\s*<value>Cancelar</value>").Should().BeTrue();
        Regex.IsMatch(Leer("Features/Usuarios/Recursos/TextosUsuarios.ca-ES.resx"), @"AsignarEmpresasVolver""[^>]*>\s*<value>Cancel·la</value>").Should().BeTrue();
    }
}
