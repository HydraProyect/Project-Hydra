using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Un <c>Boton</c> con <c>Href</c> se pinta como <c>&lt;a class="boton …"&gt;</c> y el navegador subraya los enlaces
/// por defecto (<c>base.css</c> solo le da color a <c>a</c>). bUnit no calcula estilos: se inspecciona la hoja fuente,
/// como <c>PulidoUxLoteFTests</c>. El subrayado real y el foco visible se miden en el navegador.
/// </summary>
public class BotonEnlaceSinSubrayadoCssTests
{
    private static string Css()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio");
        var css = File.ReadAllText(Path.Combine(dir!, "src", "CaeManager.Web", "Components", "DesignSystem", "Boton.razor.css"));
        return Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
    }

    private static IEnumerable<(string Selectores, string Cuerpo)> Reglas() =>
        Regex.Matches(Css(), @"(?<sel>[^{}]+)\{(?<c>[^{}]*)\}")
            .Select(m => (Regex.Replace(m.Groups["sel"].Value, @"\s+", " ").Trim(), m.Groups["c"].Value));

    [Fact]
    public void La_regla_base_del_boton_quita_el_subrayado_del_enlace()
    {
        var cuerpos = Reglas().Where(r => r.Selectores.Split(',').Select(s => s.Trim()).Contains(".boton")).Select(r => r.Cuerpo).ToList();

        cuerpos.Should().NotBeEmpty("debe existir la regla base .boton");
        // La primera regla .boton es la base, de nivel superior; las posteriores (p. ej. dentro de @media) solo valen en esa condición.
        cuerpos[0].Should().MatchRegex(@"(^|;|\s)text-decoration(-line)?\s*:\s*none\b",
            "el <a class=\"boton\"> de Boton con Href saldría subrayado por la hoja de estilos del navegador");
    }

    [Fact]
    public void Ninguna_regla_del_boton_vuelve_a_subrayar_ni_apaga_el_indicador_de_foco()
    {
        foreach (var (selectores, cuerpo) in Reglas())
        {
            cuerpo.Should().NotMatchRegex(@"text-decoration(-line)?\s*:\s*[^;]*underline", $"«{selectores}» no debe subrayar el botón");
            cuerpo.Should().NotMatchRegex(@"(^|;|\s)outline\s*:\s*(none|0)\b", $"«{selectores}» no debe quitar el contorno de :focus-visible de base.css");
        }
    }
}
