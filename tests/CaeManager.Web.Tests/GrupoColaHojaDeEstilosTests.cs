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

    private static string RutaDeLaHoja()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio para leer la hoja de estilos");
        return Path.Combine(dir!, "src", "CaeManager.Web", "Features", "Bandeja", "Components", "GrupoCola.razor.css");
    }
}
