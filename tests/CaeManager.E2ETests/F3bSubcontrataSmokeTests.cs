using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Smoke mínimo de la transición F3b-Subcontrata, construido ANTES de
/// congelar los escritores de Subcontrata (a diferencia de F3b-Cliente,
/// donde el equivalente para P331/AlcanceRolesTests se escribió/adaptó
/// después de que CI lo destapara en rojo). Objetivo explícito del
/// propietario del producto: un instrumento que permita distinguir un fallo
/// preexistente de una regresión introducida por este incremento, no cubrir
/// toda la funcionalidad de Subcontrata (ver
/// f3b-subcontrata-inventario-fresco-2026-08-26.md §8).
///
/// <para>
/// A diferencia de lo que se anticipaba en el diseño de este smoke (que
/// asumía el mismo patrón "lista principal vacía" de Cliente), la propia
/// construcción de este test destapó que <c>ObtenerSubcontratasQuery</c> Y
/// <c>ObtenerSubcontratasParaSelectorQuery</c> tuvieron que adelantarse a
/// leer Empresas — ver f3b-subcontrata-obtenersubcontratasquery-adelantada-
/// 2026-08-26.md y f3b-subcontrata-selector-adelantado-2026-08-26.md. Por
/// eso este test verifica que <c>/subcontratas</c> SÍ muestra la fila nueva
/// (no que se quede vacía como <c>/clientes</c>) — es la superficie real
/// tras esas dos correcciones, no la asumida al principio.
/// </para>
/// </summary>
[Collection("AppCollection")]
public class F3bSubcontrataSmokeTests(WebAppFixture fixture)
{
    [Fact]
    public async Task Crear_subcontrata_y_operaciones_criticas_de_F3b_funcionan()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var razonSocialSubcontrata = $"F3b Smoke Subcontrata {sufijo}";
        var apellidosTrabajador = $"F3bSmoke {sufijo}";

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);

        // --- Paso 1: crear la Subcontrata (escritor F3b) ---
        var drawer = page.Locator(".drawer-panel");
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/subcontratas");
        await page.GetByText("+ Nueva subcontrata").First.ClickAsync();
        await drawer.GetByLabel("Razón social").FillAsync(razonSocialSubcontrata);
        await drawer.GetByLabel("CIF", new LocatorGetByLabelOptions { Exact = true })
            .FillAsync(Ayudas.GenerarCifValido(9_998_501));
        await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        // --- Paso 2: aparece en /subcontratas (ObtenerSubcontratasQuery, adelantada) ---
        await page.GetByPlaceholder("Buscar por razón social o CIF…").FillAsync(razonSocialSubcontrata);
        await page.Locator(".tarjeta-fila-acordeon", new PageLocatorOptions { HasText = razonSocialSubcontrata })
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        // --- Paso 3: aparece también en /empresas (misma fila física, EsCritico/NivelServicio aparte) ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/empresas");
        await page.GetByPlaceholder("Buscar por razón social…").FillAsync(razonSocialSubcontrata);
        await page.Locator(".tarjeta-fila-acordeon", new PageLocatorOptions { HasText = razonSocialSubcontrata })
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        // --- Paso 4: operación crítica de F3b — dar de alta un Trabajador de
        // esta Subcontrata en la MISMA sesión, vía el selector poblado por
        // ObtenerSubcontratasParaSelectorQuery (adelantada). Es exactamente
        // el escenario que rompía sin el repunteo de FKs (23503) y sin
        // adelantar el selector (la Subcontrata nunca aparecía en el
        // desplegable). ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/trabajadores");
        await page.GetByText("+ Nuevo trabajador").First.ClickAsync();

        var drawerTrabajador = page.Locator(".drawer-panel");
        await drawerTrabajador.WaitForAsync(new LocatorWaitForOptions { Timeout = 5_000 });
        // El radio y el <select> comparten la misma etiqueta "Subcontrata"
        // (GetByLabel sería ambiguo); tras marcar el radio solo queda un
        // <select> visible en el drawer (rama "empresa" deja de renderizarse).
        await drawerTrabajador.GetByText("Subcontrata", new LocatorGetByTextOptions { Exact = true }).ClickAsync();
        // El clic conmuta la rama Empresa/Subcontrata del drawer vía SignalR
        // (round-trip circuito -> StateHasChanged -> parche de DOM); sin esta
        // espera, SelectOptionAsync puede intentar actuar mientras el <select>
        // de Empresa todavía no se ha desmontado. GetByRole(Combobox), no
        // .Locator("select") a secas: mismo patrón que
        // FlujoBandejaPriorizadaTests para el <select> de Empresa.
        var selectorSubcontrata = drawerTrabajador.GetByRole(AriaRole.Combobox, new LocatorGetByRoleOptions { Name = "Subcontrata" });
        await selectorSubcontrata.WaitForAsync(new LocatorWaitForOptions { Timeout = 5_000 });

        var opcionSubcontrata = selectorSubcontrata.Locator("option", new LocatorLocatorOptions { HasText = razonSocialSubcontrata });
        await opcionSubcontrata.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 10_000 });
        var valorOpcion = await opcionSubcontrata.GetAttributeAsync("value");

        // El <select> de CampoSelect fija "value" por atributo (no
        // @bind-value) y notifica a _subcontrataId vía @onchange -> interop
        // JS -> SignalR -> C#: bajo la carga de la suite completa, un solo
        // SelectOptionAsync puede no propagarse (visto en CI: el DOM queda
        // en "Selecciona una subcontrata…" pese a no lanzar ninguna
        // excepción). Reintentar hasta que el propio valor del <select> lo
        // confirme, en vez de fiarse de una espera fija.
        var confirmado = false;
        for (var intento = 0; intento < 3 && !confirmado; intento++)
        {
            await selectorSubcontrata.SelectOptionAsync(new SelectOptionValue { Value = valorOpcion });
            try
            {
                await Expect(selectorSubcontrata).ToHaveValueAsync(valorOpcion!, new LocatorAssertionsToHaveValueOptions { Timeout = 3_000 });
                confirmado = true;
            }
            catch (PlaywrightException)
            {
                // Reintenta — ver comentario de arriba.
            }
        }
        Assert.True(confirmado, "SelectOptionAsync sobre el selector de Subcontrata no se propagó a _subcontrataId tras 3 intentos");

        await drawerTrabajador.GetByLabel("Documento de identidad (DNI, NIE, TIE o pasaporte)")
            .FillAsync(Ayudas.GenerarDniValido(88_500_001));
        await drawerTrabajador.GetByLabel("Nombre", new LocatorGetByLabelOptions { Exact = true }).FillAsync("F3b");
        await drawerTrabajador.GetByLabel("Apellidos", new LocatorGetByLabelOptions { Exact = true }).FillAsync(apellidosTrabajador);
        await drawerTrabajador.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
        try
        {
            await drawerTrabajador.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });
        }
        catch (TimeoutException)
        {
            var alerta = drawerTrabajador.Locator(".alerta-formulario");
            var textoAlerta = await alerta.CountAsync() > 0 ? await alerta.InnerTextAsync() : "(sin .alerta-formulario visible)";
            throw new Exception($"El drawer de Trabajador no se cerró. Mensaje de error mostrado: {textoAlerta}");
        }

        // Confirma que el alta se guardó de verdad (no solo que el drawer se
        // cerró) y que el Trabajador queda vinculado a la Subcontrata nueva
        // — la columna "Empresa / Subcontrata" del listado muestra su razón
        // social exactamente porque el FK se resolvió contra Empresas.
        await page.GetByPlaceholder("Buscar por nombre, apellidos, alias o DNI…").FillAsync(apellidosTrabajador);
        var filaTrabajador = page.Locator(".tabla-datos tr", new PageLocatorOptions { HasText = apellidosTrabajador });
        await filaTrabajador.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        await Expect(filaTrabajador).ToContainTextAsync(razonSocialSubcontrata);
    }

    private static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);
}
