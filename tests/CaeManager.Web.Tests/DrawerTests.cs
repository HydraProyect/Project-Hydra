using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// Cubre a nivel de componente la lógica de estado detrás del fix del bug
/// #7 del backlog (ver ROADMAP.md, "Drawer se cierra al seleccionar
/// texto"): Drawer.razor no usa JS interop en absoluto — todo el mecanismo
/// vive en el campo <c>_mouseDownEnSuperposicion</c> y los manejadores
/// @onmousedown/@onclick de Drawer.razor, así que sí es testable aquí sin
/// navegador real.
///
/// Aviso importante sobre lo que este test NO reproduce: el bug real ocurre
/// porque el navegador dispara el evento "click" sobre el ancestro común de
/// los elementos que recibieron mousedown y mouseup (aquí, la superposición),
/// incluso si el mousedown empezó dentro del panel — es un comportamiento
/// nativo del DOM, no algo que Blazor simule. bUnit despacha cada evento
/// directamente al manejador del elemento exacto que se le indica, sin
/// recorrer el árbol real como haría un navegador. Por eso aquí se invocan
/// los manejadores del panel y de la superposición por separado para probar
/// la máquina de estados (_mouseDownEnSuperposicion) de forma aislada — el
/// comportamiento real end-to-end de arrastrar el ratón y que el evento
/// "click" del navegador aterrice en el elemento correcto sigue probado por
/// RegresionesTests (Playwright) en CaeManager.E2ETests, que es la fuente de
/// verdad para ese gesto real de usuario.
/// </summary>
public class DrawerTests : BunitContext
{
    [Fact]
    public void Mousedown_en_el_panel_seguido_de_click_en_la_superposicion_no_cierra_el_drawer()
    {
        var visible = true;
        var cut = Render<Drawer>(parametros => parametros
            .Add(p => p.Visible, visible)
            .Add(p => p.VisibleChanged, v => visible = v)
            .Add(p => p.Titulo, "Prueba")
            .AddChildContent("<p>Contenido</p>"));

        // Simula que el gesto de selección de texto empezó dentro del panel:
        // su propio @onmousedown pone _mouseDownEnSuperposicion a false.
        cut.Find(".drawer-panel").MouseDown(new MouseEventArgs());

        // El click que el navegador termina disparando sobre la superposición
        // (el ancestro común del mousedown/mouseup reales) no debe cerrar el
        // drawer, porque _mouseDownEnSuperposicion sigue en false.
        cut.Find(".drawer-superposicion").Click(new MouseEventArgs());

        visible.Should().BeTrue("seleccionar texto dentro del panel no debe cerrar el drawer");
    }

    [Fact]
    public void Mousedown_y_click_en_la_superposicion_si_cierra_el_drawer()
    {
        bool? valorNotificado = null;
        var cut = Render<Drawer>(parametros => parametros
            .Add(p => p.Visible, true)
            .Add(p => p.VisibleChanged, v => valorNotificado = v)
            .Add(p => p.Titulo, "Prueba")
            .AddChildContent("<p>Contenido</p>"));

        // Un clic genuino fuera del panel: tanto el mousedown como el click
        // ocurren sobre la propia superposición, sin pasar por el panel.
        cut.Find(".drawer-superposicion").MouseDown(new MouseEventArgs());
        cut.Find(".drawer-superposicion").Click(new MouseEventArgs());

        valorNotificado.Should().BeFalse("un clic fuera del panel sí debe cerrar el drawer");
    }

    [Fact]
    public void CerrarAlHacerClicFuera_en_false_ignora_el_clic_fuera()
    {
        bool? valorNotificado = null;
        var cut = Render<Drawer>(parametros => parametros
            .Add(p => p.Visible, true)
            .Add(p => p.VisibleChanged, v => valorNotificado = v)
            .Add(p => p.Titulo, "Prueba")
            .Add(p => p.CerrarAlHacerClicFuera, false)
            .AddChildContent("<p>Contenido</p>"));

        // Mientras hay un error de validación visible (ver Clientes.razor,
        // CerrarAlHacerClicFuera="@(_mensajeErrorFormulario is null && ...)")
        // ni siquiera un clic fuera genuino debe cerrar el drawer.
        cut.Find(".drawer-superposicion").MouseDown(new MouseEventArgs());
        cut.Find(".drawer-superposicion").Click(new MouseEventArgs());

        valorNotificado.Should().BeNull("CerrarAlHacerClicFuera=false debe ignorar cualquier clic fuera");
    }
}
