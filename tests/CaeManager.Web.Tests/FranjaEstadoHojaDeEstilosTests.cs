using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// El botón de estado vacío de la franja sigue siendo un control activo (se puede marcar y desmarcar): su texto debe
/// llegar al 4,5:1 de WCAG 1.4.3. Una <c>opacity</c> sobre el botón lo dejaba a 2,92:1 en oscuro (Centro 360,
/// 2026-11-08). bUnit no calcula estilos, así que se congela la hoja con control positivo.
/// </summary>
public class FranjaEstadoHojaDeEstilosTests
{
    private const string Vacio = @"\.franja-estado-boton-vacio:not\(\[aria-pressed=""true""\]\)";

    [Fact]
    public void El_boton_vacio_no_atenua_su_texto_con_opacidad()
    {
        var reglasConOpacidad = Regex.Matches(Leer(), @"(?<selector>[^{}]+)\{(?<cuerpo>[^}]*)\}")
            .Where(m => m.Groups["selector"].Value.Contains("franja-estado-boton-vacio", StringComparison.Ordinal)
                        && Regex.IsMatch(m.Groups["cuerpo"].Value, @"(?<![\w-])opacity\s*:"))
            .SelectMany(m => m.Groups["selector"].Value.Split(','))
            .Select(s => s.Trim())
            .Where(s => s.Contains("franja-estado-boton-vacio", StringComparison.Ordinal))
            .ToList();

        reglasConOpacidad.Should().NotBeEmpty("control positivo: el punto del botón vacío sí se atenúa con opacidad");
        reglasConOpacidad.Should().OnlyContain(s => s.EndsWith(".franja-estado-punto", StringComparison.Ordinal),
            "una opacidad en cualquier otro selector del botón vacío atenúa también el nombre del estado");
    }

    [Fact]
    public void El_boton_vacio_atenua_solo_el_punto_y_la_cifra()
    {
        var css = Leer();

        Cuerpo(css, Vacio + @" \.franja-estado-punto").Should().MatchRegex(@"opacity\s*:\s*0?\.45");
        Cuerpo(css, Vacio + @" \.franja-estado-recuento").Should().Contain("color: var(--color-text-muted)");
    }

    private static string Cuerpo(string css, string patronSelector)
    {
        var m = Regex.Match(css, @"(?m)^" + patronSelector + @"\s*\{(?<cuerpo>[^}]*)\}");
        m.Success.Should().BeTrue($"control positivo: existe la regla {patronSelector}");
        return m.Groups["cuerpo"].Value;
    }

    private static string Leer()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio para leer las hojas de estilo");
        return File.ReadAllText(Path.Combine(dir!, "src", "CaeManager.Web", "Components", "DesignSystem", "FranjaEstado.razor.css"));
    }
}
