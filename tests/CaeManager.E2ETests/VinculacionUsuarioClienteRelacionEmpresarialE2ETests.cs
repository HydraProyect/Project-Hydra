using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// F4.2a de punta a punta, con navegador real: crea un usuario de portal
/// (rol Cliente) por el flujo real de Usuarios.razor — vinculándolo por CIF
/// a una Empresa creada hoy, nunca a la tabla legacy Clientes, que ya no
/// recibe altas desde F3b (ver ApplicationUser.ClienteId, doc-comment) — y
/// comprueba que, al iniciar sesión como ese usuario, ve exactamente lo que
/// RelacionEmpresarial dice que debe ver: la Empresa y la Subcontrata que
/// sirven a su Cliente, y NO las que sirven a un Cliente ajeno del mismo
/// tenant. Ningún E2E ejercitaba esta ruta de escritura hasta ahora — el
/// único E2E de rol Cliente existente usa el usuario ya sembrado por
/// DatosPruebaSeeder, vinculado directamente a un Empresa.Id, sin pasar
/// nunca por BuscarEmpresaPorCifQuery/Usuarios.razor.
/// </summary>
[Collection("AppCollection")]
public class VinculacionUsuarioClienteRelacionEmpresarialE2ETests(WebAppFixture fixture)
{
    [Fact]
    public async Task Usuario_Cliente_creado_por_CIF_ve_su_alcance_real_y_no_el_de_otro_cliente()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        // Sin palabras compartidas entre sí a propósito: el buscador del
        // SelectorMultiple coincide por palabra suelta, no por substring
        // contiguo — "Cliente" y "Otro Cliente" harían match cruzado.
        var razonSocialCliente = $"F4.2a Iberojet {sufijo}";
        var razonSocialEmpresaPropia = $"F4.2a Refrielectric {sufijo}";
        var razonSocialSubcontrata = $"F4.2a Termico {sufijo}";
        var razonSocialOtroCliente = $"F4.2a Timonel {sufijo}";
        var razonSocialEmpresaAjena = $"F4.2a Contrapiso {sufijo}";
        var emailPortal = $"portal.f4.2a.{sufijo}@iberojet.test";
        // La contrasena la elige el propio usuario al activar su cuenta: el
        // alta ya no fija ninguna ni la envia por correo.
        const string contrasenaNueva = "NuevaF4.2a#2026";

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        var drawer = page.Locator(".drawer-panel");

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);

        // --- Cliente real (creado hoy: solo existe como Empresa, nunca en la tabla legacy Clientes) ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes");
        await page.GetByText("+ Nuevo cliente").First.ClickAsync();
        await drawer.GetByLabel("Razón social").FillAsync(razonSocialCliente);
        await drawer.GetByLabel("CIF", new LocatorGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_995_001));
        await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        // --- Un segundo Cliente, ajeno — control negativo ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes");
        await page.GetByText("+ Nuevo cliente").First.ClickAsync();
        await drawer.GetByLabel("Razón social").FillAsync(razonSocialOtroCliente);
        await drawer.GetByLabel("CIF", new LocatorGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_995_004));
        await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        // --- Empresa propia que sirve al Cliente real (dual-write F4 -> RelacionEmpresarial) ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/empresas");
        await page.GetByText("+ Nueva empresa").First.ClickAsync();
        await drawer.GetByLabel("Razón social").FillAsync(razonSocialEmpresaPropia);
        await drawer.GetByLabel("CIF", new LocatorGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_995_002));
        await MarcarCasillaEnSelectorMultipleAsync(drawer, "Clientes con los que trabaja", razonSocialCliente);
        await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        // --- Empresa ajena que sirve solo al OTRO Cliente — control negativo ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/empresas");
        await page.GetByText("+ Nueva empresa").First.ClickAsync();
        await drawer.GetByLabel("Razón social").FillAsync(razonSocialEmpresaAjena);
        await drawer.GetByLabel("CIF", new LocatorGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_995_005));
        await MarcarCasillaEnSelectorMultipleAsync(drawer, "Clientes con los que trabaja", razonSocialOtroCliente);
        await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        // --- Subcontrata que sirve al Cliente real (dual-write F4 -> RelacionEmpresarial) ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/subcontratas");
        await page.GetByText("+ Nueva subcontrata").First.ClickAsync();
        await drawer.GetByLabel("Razón social").FillAsync(razonSocialSubcontrata);
        await drawer.GetByLabel("CIF", new LocatorGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_995_003));
        await MarcarCasillaEnSelectorMultipleAsync(drawer, "Clientes que la contrataron", razonSocialCliente);
        await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        // --- El comportamiento que F4.2a corrige: vincular un usuario de
        // portal Cliente por CIF a esa Empresa, vía el flujo real de
        // Usuarios.razor (BuscarEmpresaPorCifQuery, no BuscarClientePorCifQuery). ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/usuarios");
        await page.GetByText("+ Nuevo usuario").First.ClickAsync();
        await drawer.GetByLabel("Correo").FillAsync(emailPortal);
        await drawer.GetByLabel("Nombre completo").FillAsync($"Portal {razonSocialCliente}");
        await drawer.GetByLabel("Rol").SelectOptionAsync(new SelectOptionValue { Value = "Cliente" });

        var campoCif = drawer.GetByLabel("CIF del cliente a vincular");
        await campoCif.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        // Busca por CIF exacto, no por razón social — el mismo CIF generado
        // para el Cliente real de arriba.
        await campoCif.FillAsync(Ayudas.GenerarCifValido(9_995_001));
        await drawer.GetByText($"✓ {razonSocialCliente}").WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        // CampoTexto.razor re-dispara ValorChanged incondicionalmente al
        // perder el foco (ManejarBlurAsync), incluso si el debounce ya
        // notificó el mismo valor — el propio clic en "Guardar" le quita el
        // foco al campo CIF y relanza BuscarEmpresaPorCifQuery justo cuando
        // GuardarAsync comprueba _clienteEncontrado. Se le quita el foco
        // aquí, a propósito, y se espera a que la confirmación se re-asiente
        // ANTES del clic — así "Guardar" ya no dispara ningún blur nuevo.
        await campoCif.BlurAsync();
        await drawer.GetByText($"✓ {razonSocialCliente}").WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        // --- Activación: el usuario establece SU contraseña desde el enlace ---
        // El alta ya no fija ninguna contraseña ni la envía por correo: la
        // cuenta nace sin contraseña y quien la usa la establece desde un
        // enlace de un solo uso. El mismo enlace queda en pantalla para quien
        // hizo el alta, y de ahí lo toma este test — leerlo aquí es además la
        // única forma de ejercitar el flujo completo sin un buzón que leer.
        var enlaceActivacion = await page.Locator(".enlace-activacion").InnerTextAsync();
        Assert.Contains("/cuenta/restablecer-contrasena", enlaceActivacion);

        await using var contextoPortal = await fixture.Browser.NewContextAsync();
        var paginaPortal = await contextoPortal.NewPageAsync();
        await paginaPortal.GotoAsync(enlaceActivacion.Trim());

        // Señal concreta de que la página de activación renderizó, en vez de
        // esperar un estado de carga: bajo carga de CI la navegación se
        // pasaba de los 15 s y el test moría antes de tocar nada de lo que
        // venía a comprobar (el TimeoutException de Frame.WaitForLoadStateAsync
        // que bloqueaba la cola de merge).
        await paginaPortal.Locator("#password-nueva")
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

        await paginaPortal.FillAsync("#password-nueva", contrasenaNueva);
        await paginaPortal.FillAsync("#password-confirmar", contrasenaNueva);

        // El boton nace DESHABILITADO —su disabled depende de que la politica
        // de contrasena se cumpla y las dos coincidan— y solo se habilita
        // cuando el circuito ha procesado lo que acabamos de escribir. Se
        // afirma explicitamente en vez de dejar que ClickAsync espere por
        // accionabilidad: asi un fallo dice "el boton sigue deshabilitado",
        // que nombra la causa, en vez de un timeout mudo de 30 s.
        //
        // Esta espera destapo un defecto real de producto: la pagina no
        // declaraba @rendermode, asi que ese disabled se evaluaba en servidor
        // con la contraseña vacia y NADA podia habilitarlo. Restablecer la
        // contrasena era imposible para cualquier usuario.
        await Assertions.Expect(paginaPortal.Locator("button[type=\"submit\"]"))
            .ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 30_000 });

        await paginaPortal.ClickAsync("button[type=\"submit\"]");

        // Esperar la confirmación ANTES de navegar. ClickAsync vuelve en cuanto
        // despacha el clic, no cuando el manejador de Blazor ha terminado: sin
        // esta espera, el GotoAsync del login siguiente se llevaba por delante
        // el envío a medio hacer, la contraseña no llegaba a establecerse y el
        // login fallaba después — con el error en el sitio equivocado.
        //
        // "Contraseña actualizada" solo se renderiza tras ResetPasswordAsync
        // correcto y el SignOutAsync que le sigue, así que es la señal de que
        // la cuenta YA tiene contraseña y la sesión está limpia para entrar.
        await Assertions.Expect(paginaPortal.GetByText("Contraseña actualizada"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });

        // Y ahora sí, el login normal. Que este paso funcione prueba lo que
        // importa del cambio: la cuenta no tenía contraseña hasta que su dueño
        // le puso una.
        await Ayudas.IniciarSesionAsync(paginaPortal, fixture.BaseUrl, emailPortal, contrasenaNueva);

        // --- El alcance real, derivado de RelacionEmpresarial vía AlcanceDatosService ---
        //
        // GotoAsync y NO NavegarYEsperarAsync: ese ayudante añade
        // WaitForLoadStateAsync(NetworkIdle), que en Blazor Server no dice lo
        // que parece — la actividad de reconexión del circuito se solapa con
        // esa espera y la resuelve antes de tiempo (ya documentado en
        // FlujoAltaYRevocacionDelegacionTests). El daño no es esperar de
        // menos: es que a continuación se rellena el buscador, y si el
        // circuito todavía no ha rehidratado ese input no está enlazado, así
        // que el filtro NO se aplica nunca y la espera de la fila agota sus
        // 15 s. Ese era el fallo intermitente que bloqueaba la cola de merge
        // de varios módulos (33 de 34 tests en verde, siempre este).
        //
        // La señal concreta de que el circuito ya está vivo Y la consulta de
        // alcance se resolvió es que haya al menos una fila renderizada.
        // Esperar a un elemento que solo existe tras ambas cosas es lo que
        // NetworkIdle no puede prometer.
        await paginaPortal.GotoAsync($"{fixture.BaseUrl}/empresas");
        await paginaPortal.Locator(".tarjeta-fila-acordeon").First
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

        await paginaPortal.GetByPlaceholder("Buscar por razón social…").FillAsync(razonSocialEmpresaPropia);
        await paginaPortal.Locator(".tarjeta-fila-acordeon", new PageLocatorOptions { HasText = razonSocialEmpresaPropia })
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        // Control negativo: la Empresa que sirve al OTRO cliente no aparece,
        // ni siquiera filtrando por su propio nombre exacto.
        await paginaPortal.GetByPlaceholder("Buscar por razón social…").FillAsync(razonSocialEmpresaAjena);
        await Expect(paginaPortal.Locator(".tarjeta-fila-acordeon", new PageLocatorOptions { HasText = razonSocialEmpresaAjena }))
            .Not.ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 5_000 });

        // Misma razón que arriba: señal concreta de circuito vivo antes de
        // tocar el buscador, no silencio de red.
        await paginaPortal.GotoAsync($"{fixture.BaseUrl}/subcontratas");
        await paginaPortal.Locator(".tarjeta-fila-acordeon").First
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

        await paginaPortal.GetByPlaceholder("Buscar por razón social o CIF…").FillAsync(razonSocialSubcontrata);
        await paginaPortal.Locator(".tarjeta-fila-acordeon", new PageLocatorOptions { HasText = razonSocialSubcontrata })
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
    }

    /// <summary>
    /// Algunos drawers (Subcontratas.razor) tienen DOS SelectorMultiple a la
    /// vez ("Clientes que la contrataron" + "Empresas a las que presta
    /// servicio") — y el catálogo de Empresas es global a propósito (ver
    /// ObtenerEmpresasParaSelectorQuery), así que un Cliente recién creado
    /// (que también es una fila de Empresas) puede aparecer como opción en
    /// AMBOS selectores del mismo drawer. Buscar por <c>drawer.GetByLabel</c>
    /// a secas es ambiguo ahí — hay que acotar primero al contenedor
    /// ".campo" del selector correcto, identificado por su propia etiqueta.
    /// </summary>
    private static async Task MarcarCasillaEnSelectorMultipleAsync(ILocator drawer, string etiquetaSelector, string nombreElemento)
    {
        var etiquetaLocator = drawer.GetByText(etiquetaSelector, new LocatorGetByTextOptions { Exact = true });
        var selector = etiquetaLocator.Locator("xpath=ancestor::div[contains(@class,'campo')][1]");

        await selector.GetByPlaceholder("Buscar…").FillAsync(nombreElemento);

        var casilla = selector.GetByRole(AriaRole.Checkbox, new LocatorGetByRoleOptions { Name = nombreElemento });
        await Expect(casilla).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 10_000 });
        await casilla.CheckAsync();
    }

    private static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);
}
