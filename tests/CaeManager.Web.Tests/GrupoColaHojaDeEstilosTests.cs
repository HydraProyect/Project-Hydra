using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// La tarjeta de un grupo de la cola no puede recortar a sus hijos: los recuentos
/// de su cabecera son <c>VentanaContexto</c>, cuyo panel se abre hacia arriba y
/// sale de la tarjeta. Con <c>overflow: hidden</c> el panel del primer grupo de
/// Inicio quedaba cortado por el borde superior y parecía estar detrás del fondo.
/// </summary>
public class GrupoColaHojaDeEstilosTests
{
    [Fact]
    public void La_tarjeta_del_grupo_no_recorta_el_panel_de_contexto_de_su_cabecera()
    {
        var css = File.ReadAllText(RutaDeLaHoja());

        // Control positivo: la regla que se inspecciona existe. Sin esto, un
        // renombrado de la clase dejaría el test verde sin mirar nada.
        css.Should().MatchRegex(@"(?m)^\.grupo-cola\s*\{");

        var regla = System.Text.RegularExpressions.Regex.Match(css, @"(?m)^\.grupo-cola\s*\{(?<cuerpo>[^}]*)\}").Groups["cuerpo"].Value;
        regla.Should().NotMatchRegex(@"overflow\s*:\s*(hidden|clip|auto|scroll)",
            "el panel de VentanaContexto de la cabecera se abre fuera de la tarjeta y quedaría cortado");
    }

    /// <summary>
    /// Inicio a 1024 y 375 px: con los badges en <c>flex: none</c> robaban el ancho al título (el nombre de la
    /// Empresa salía en tres líneas, el resumen con una palabra por línea) y se cortaban por la derecha. El bloque
    /// de badges tiene que poder encoger y envolver, el título tiene una base mínima y la cabecera envuelve.
    /// </summary>
    [Fact]
    public void Los_badges_de_la_cabecera_pueden_encoger_y_envolver_y_el_titulo_conserva_una_base()
    {
        var css = File.ReadAllText(RutaDeLaHoja());

        var badges = CuerpoDe(css, ".grupo-cola-badges");
        badges.Should().NotMatchRegex(@"flex\s*:\s*(none|0\s+0)", "con flex: none el bloque no encoge y roba el ancho al título");
        badges.Should().MatchRegex(@"flex\s*:\s*0\s+1\s+auto");
        badges.Should().MatchRegex(@"min-width\s*:\s*0");
        badges.Should().MatchRegex(@"flex-wrap\s*:\s*wrap");

        var info = CuerpoDe(css, ".grupo-cola-info");
        info.Should().MatchRegex(@"flex\s*:\s*1\s+1\s+(?!0)\d*\.?\d+(rem|em|px|ch)\b",
            "el título necesita una base de ancho positiva: con base 0 (flex: 1, 1 1 0, auto) los badges vuelven a competir por el ancho en una sola línea");
        info.Should().MatchRegex(@"min-width\s*:\s*0");

        CuerpoDe(css, ".grupo-cola-cabecera").Should().MatchRegex(@"flex-wrap\s*:\s*wrap",
            "sin envolver, el título y los badges comparten una sola línea a cualquier ancho");

        // Contenedor de consulta prohibido en la tarjeta: ancla las position: fixed de la hoja móvil de VentanaContexto.
        css.Should().NotMatchRegex(@"container-type\s*:", "container-type haría de la tarjeta el bloque contenedor de la hoja inferior de VentanaContexto");
    }

    private static string CuerpoDe(string css, string selector)
    {
        var m = System.Text.RegularExpressions.Regex.Match(css, @"(?m)^" + System.Text.RegularExpressions.Regex.Escape(selector) + @"\s*\{(?<cuerpo>[^}]*)\}");
        m.Success.Should().BeTrue($"la regla {selector} existe en la hoja (control positivo del instrumento)");
        return m.Groups["cuerpo"].Value;
    }

    private static string RutaDeLaHoja()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio para leer la hoja de estilos");
        return Path.Combine(dir!, "src", "CaeManager.Web", "Features", "Bandeja", "Components", "GrupoCola.razor.css");
    }
}
