using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// La cabecera rediseñada (menú de usuario con avatar, buscador con atajo, campana de avisos,
/// cabecera fija con sombra y banda de avisos). Comprueba el comportamiento interactivo que no
/// tiene prueba de componente: el menú de <c>wwwroot/js/cabecera.js</c> (apertura, Esc, clic fuera,
/// teclado) y la sombra al desplazar. El resto —iniciales, migas, chip, campana— lo cubren los
/// tests de componente de Web.Tests.
/// </summary>
[Collection("AppCollection")]
public class CabeceraTests(WebAppFixture fixture)
{
    [Fact]
    public async Task El_menu_de_usuario_abre_con_el_avatar_y_cierra_con_Esc_devolviendo_el_foco()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailGestorRefrielectric, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.DescartarNotificacionesPendientesAsync(page);

        var avatar = page.Locator("#menu-cuenta-boton");
        var panel = page.Locator("#menu-cuenta-panel");
        await Assertions.Expect(avatar).ToHaveAttributeAsync("aria-expanded", "false");
        await Assertions.Expect(panel).ToBeHiddenAsync();

        await Ayudas.AbrirMenuDeUsuarioAsync(page);
        await Assertions.Expect(avatar).ToHaveAttributeAsync("aria-expanded", "true");
        await Assertions.Expect(panel.GetByRole(AriaRole.Menuitem, new() { Name = "Cerrar sesión" })).ToBeVisibleAsync();

        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(avatar).ToHaveAttributeAsync("aria-expanded", "false");
        await Assertions.Expect(panel).ToBeHiddenAsync();
        await Assertions.Expect(avatar).ToBeFocusedAsync();
    }

    [Fact]
    public async Task Un_clic_fuera_cierra_el_menu_de_usuario()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailGestorRefrielectric, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.DescartarNotificacionesPendientesAsync(page);

        await Ayudas.AbrirMenuDeUsuarioAsync(page);
        await page.Locator("main#contenido-principal").ClickAsync(new() { Position = new Position { X = 5, Y = 5 } });

        await Assertions.Expect(page.Locator("#menu-cuenta-boton")).ToHaveAttributeAsync("aria-expanded", "false");
    }

    [Fact]
    public async Task La_cabecera_gana_sombra_al_desplazar_la_pagina_y_la_pierde_arriba()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await page.SetViewportSizeAsync(1280, 400);
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailGestorRefrielectric, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.DescartarNotificacionesPendientesAsync(page);

        var cabecera = page.Locator(".cabecera-fija");
        await Assertions.Expect(cabecera).Not.ToHaveAttributeAsync("data-desplazada", "");

        // Se fuerza altura: la pantalla de aterrizaje puede ser más corta que el visor.
        await page.EvaluateAsync("document.body.style.minHeight = '3000px'; window.scrollTo(0, 400)");
        await Assertions.Expect(cabecera).ToHaveAttributeAsync("data-desplazada", "");

        await page.EvaluateAsync("window.scrollTo(0, 0)");
        await Assertions.Expect(cabecera).Not.ToHaveAttributeAsync("data-desplazada", "");
    }

    [Fact]
    public async Task En_movil_la_cabecera_deja_solo_iconos_y_el_menu_de_usuario_sigue_ofreciendo_cerrar_sesion()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await page.SetViewportSizeAsync(375, 812);
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailGestorRefrielectric, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.DescartarNotificacionesPendientesAsync(page);

        await Assertions.Expect(page.Locator(".boton-buscador-global-texto")).ToBeHiddenAsync();

        var desborde = await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth");
        Assert.False(desborde, "la cabecera en móvil no puede provocar desplazamiento horizontal");

        await Ayudas.AbrirMenuDeUsuarioAsync(page);
        await Assertions.Expect(page.Locator(".boton-cerrar-sesion")).ToBeVisibleAsync();
    }
}
