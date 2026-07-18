using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Un test por cada bug real ya corregido (ver ROADMAP.md, "Iniciativa de
/// hardening" § Tests E2E automatizados) — para que no puedan volver sin
/// que un test lo note. Por ahora cubre el bug #7 del backlog: el Drawer se
/// cerraba al seleccionar texto con el ratón dentro del panel, porque el
/// evento "click" del navegador se dispara sobre el ancestro común de
/// mousedown/mouseup (la superposición) cuando el arrastre termina fuera
/// del panel, aunque el gesto empezara dentro (ver Drawer.razor,
/// _mouseDownEnSuperposicion).
/// </summary>
[Collection("AppCollection")]
public class RegresionesTests(WebAppFixture fixture)
{
    [Fact]
    public async Task Seleccionar_texto_dentro_del_drawer_no_lo_cierra_pero_clic_fuera_si()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);

        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes");
        await page.GetByText("+ Nuevo cliente").ClickAsync();

        var panel = page.Locator(".drawer-panel");
        var superposicion = page.Locator(".drawer-superposicion");
        await panel.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15_000 });

        var cajaPanel = await panel.BoundingBoxAsync() ?? throw new InvalidOperationException("El panel del drawer no tiene bounding box.");
        var cajaSuperposicion = await superposicion.BoundingBoxAsync() ?? throw new InvalidOperationException("La superposición no tiene bounding box.");

        // --- Arrastrar para seleccionar texto: mousedown dentro del panel
        // (sobre el título), arrastre y mouseup fuera de él (sobre la
        // superposición) — reproduce exactamente el gesto de "seleccionar
        // texto y soltar el ratón un poco más allá del borde del panel". ---
        var inicioX = cajaPanel.X + cajaPanel.Width / 2;
        var inicioY = cajaPanel.Y + 20; // sobre el <h2> del título, dentro del header del panel.
        var finX = cajaPanel.X + cajaPanel.Width + 40; // claramente fuera del panel, pero dentro de la superposición.
        var finY = inicioY;

        await page.Mouse.MoveAsync(inicioX, inicioY);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(finX, finY, new MouseMoveOptions { Steps = 10 });
        await page.Mouse.UpAsync();

        // El drawer sigue abierto: seleccionar texto no debe cerrarlo.
        await panel.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 2_000 });

        // --- Ahora un clic genuino fuera del panel (mousedown Y mouseup sobre
        // la superposición, sin pasar por dentro del panel) sí debe cerrarlo. ---
        var puntoFueraX = cajaSuperposicion.X + 10;
        var puntoFueraY = cajaSuperposicion.Y + 10;

        await page.Mouse.MoveAsync(puntoFueraX, puntoFueraY);
        await page.Mouse.DownAsync();
        await page.Mouse.UpAsync();

        await panel.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 5_000 });
    }
}
