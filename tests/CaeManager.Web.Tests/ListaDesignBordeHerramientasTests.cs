using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Decisión de Chris (2026-11-08, revisión 1:1 de la zona C, fila C4): el borde de «Expandir todo» y de los botones de
/// <c>.barra-herramientas-lista</c> de las listas usa <c>--color-border-control</c>, no <c>--color-border-strong</c>, porque
/// un control que no se nombra solo por su borde necesita 3:1 contra su fondo (WCAG 1.4.11). bUnit no calcula estilos:
/// se congela la hoja <c>lista-design.css</c> con control positivo y se mide el contraste de los tokens en claro y en oscuro.
/// </summary>
public class ListaDesignBordeHerramientasTests
{
    private const string Boton = @"\.lista-design\.lista-design \.barra-herramientas-lista button";

    [Fact]
    public void El_borde_en_reposo_del_boton_de_herramientas_es_el_borde_de_control()
    {
        Cuerpo(Leer("lista-design.css"), Boton + @"(?!:)")
            .Should().MatchRegex(@"border\s*:\s*1px solid var\(--color-border-control\)")
            .And.NotContain("--color-border-strong");
    }

    [Fact]
    public void El_borde_bajo_el_puntero_tampoco_baja_al_borde_fuerte()
    {
        var hover = Cuerpo(Leer("lista-design.css"), Boton + ":hover");

        hover.Should().MatchRegex(@"border-color\s*:\s*var\(--color-border-control\)");
        hover.Should().NotContain("--color-border-strong");
    }

    [Fact]
    public void El_borde_de_control_llega_a_3_a_1_contra_la_superficie_en_claro_y_en_oscuro()
    {
        var tokens = Leer("tokens.css");
        var claro = Regex.Match(tokens, @"(?ms)^:root \{(?<c>.*?)^\}").Groups["c"].Value;
        var oscuro = Regex.Match(tokens, @"(?ms)^:root\[data-theme='oscuro'\] \{(?<c>.*?)^\}").Groups["c"].Value;
        claro.Should().NotBeEmpty("control positivo: existe el bloque de tokens claro");
        oscuro.Should().NotBeEmpty("control positivo: existe el bloque de tokens oscuro");

        var bordeClaro = Color(claro, "--color-neutral-500"); // --color-border-control: var(--color-neutral-500)
        claro.Should().MatchRegex(@"--color-border-control\s*:\s*var\(--color-neutral-500\)");
        Contraste(bordeClaro, Color(claro, "--color-neutral-0")).Should().BeGreaterThanOrEqualTo(3.0, "borde de control sobre la superficie, tema claro");

        Contraste(Color(oscuro, "--color-border-control"), Color(oscuro, "--color-surface"))
            .Should().BeGreaterThanOrEqualTo(3.0, "borde de control sobre la superficie, tema oscuro");
    }

    private static string Cuerpo(string css, string patronSelector)
    {
        var m = Regex.Match(css, @"(?m)^" + patronSelector + @"\s*\{(?<cuerpo>[^}]*)\}");
        m.Success.Should().BeTrue($"control positivo: existe la regla {patronSelector}");
        return m.Groups["cuerpo"].Value;
    }

    private static (double R, double G, double B) Color(string bloque, string token)
    {
        var m = Regex.Match(bloque, Regex.Escape(token) + @"\s*:\s*#(?<h>[0-9a-fA-F]{6})\s*;");
        m.Success.Should().BeTrue($"control positivo: {token} es un color hexadecimal literal");
        var h = m.Groups["h"].Value;
        double Canal(int i) => int.Parse(h.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
        return (Canal(0), Canal(2), Canal(4));
    }

    private static double Luminancia((double R, double G, double B) c)
    {
        static double L(double v) => v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        return 0.2126 * L(c.R) + 0.7152 * L(c.G) + 0.0722 * L(c.B);
    }

    private static double Contraste((double R, double G, double B) a, (double R, double G, double B) b)
    {
        var (la, lb) = (Luminancia(a), Luminancia(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static string Leer(string fichero)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio para leer las hojas de estilo");
        return File.ReadAllText(Path.Combine(dir!, "src", "CaeManager.Web", "wwwroot", "css", fichero));
    }
}
