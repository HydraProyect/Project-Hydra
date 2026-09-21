using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// El menú «⋯» de las listas se veía mal en Clientes: la tabla recortaba el panel
/// (sobresale de la fila cuando hay pocas filas) y los ítems, que son un componente
/// hijo declarado por la página, no recibían los estilos aislados de
/// <c>MenuAcciones.razor.css</c> y se pintaban como botones nativos.
/// </summary>
public class MenuAccionesHojaDeEstilosTests
{
    [Fact]
    public void La_tabla_de_datos_no_recorta_el_panel_del_menu_de_acciones()
    {
        var css = File.ReadAllText(Ruta("wwwroot", "css", "list-page.css"));

        // Control positivo: la regla inspeccionada existe.
        css.Should().MatchRegex(@"(?m)^\.tabla-datos\s*\{");

        var regla = Regex.Match(css, @"(?m)^\.tabla-datos\s*\{(?<cuerpo>[^}]*)\}").Groups["cuerpo"].Value;
        // Se quitan los comentarios: el que explica el cambio nombra la propiedad.
        regla = Regex.Replace(regla, @"/\*.*?\*/", "", RegexOptions.Singleline);
        regla.Should().NotMatchRegex(@"overflow\s*:\s*(hidden|clip|auto|scroll)",
            "el panel de MenuAcciones es absoluto y sobresale de la tabla en las listas de pocas filas");
    }

    [Fact]
    public void Los_estilos_de_los_items_del_menu_alcanzan_al_boton_del_componente_hijo()
    {
        var css = File.ReadAllText(Ruta("Components", "DesignSystem", "MenuAcciones.razor.css"));

        css.Should().MatchRegex(@"::deep\s+\.menu-acciones-item\s*\{", "control positivo: la regla del ítem existe");
        // Toda regla del ítem debe llevar ::deep; sin él el identificador de CSS aislado
        // del panel no llega al <button> de ItemMenuAccion.
        var sinDeep = Regex.Matches(css, @"(?m)^(?<sel>\.menu-acciones-item[^{]*)\{")
            .Select(m => m.Groups["sel"].Value.Trim());
        sinDeep.Should().BeEmpty("las reglas de .menu-acciones-item sin ::deep no aplican al ítem");
    }

    private static string Ruta(params string[] partes)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio para leer la hoja de estilos");
        return Path.Combine([dir!, "src", "CaeManager.Web", .. partes]);
    }
}
