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
        const string contrasenaTemporal = "TemporalF4.2a#1";
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
        await drawer.GetByLabel("Contraseña temporal").FillAsync(contrasenaTemporal);
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

        // --- Sesión nueva: iniciar sesión como el portal-user recién creado ---
        // No se reutiliza Ayudas.IniciarSesionAsync: ese helper espera
        // ".nav-principal" al final, pero DebeCambiarContrasena=true (todo
        // alta desde Usuarios.razor) hace que el login aterrice primero en
        // /cuenta/cambiar-contrasena, sin ese elemento.
        await using var contextoPortal = await fixture.Browser.NewContextAsync();
        var paginaPortal = await contextoPortal.NewPageAsync();
        await paginaPortal.GotoAsync($"{fixture.BaseUrl}/cuenta/iniciar-sesion");
        await paginaPortal.FillAsync("#email", emailPortal);
        await paginaPortal.FillAsync("#password", contrasenaTemporal);
        await paginaPortal.ClickAsync("button[type=\"submit\"]");

        // ESTE es el punto que la traza del fallo intermitente señalaba
        // (línea 128 de la versión anterior): WaitForURLAsync llama por dentro
        // a WaitForLoadStateAsync, que es el marco de Playwright que aparecía
        // en el TimeoutException. Esperar la URL exige que la navegación
        // COMPLETA se asiente; bajo carga de CI, el login —hash de contraseña,
        // cookie, redirección con forceLoad— se pasaba de los 15 s y el test
        // moría antes de tocar nada de lo que venía a comprobar.
        //
        // Se espera el campo del formulario en lugar de la URL: es una señal
        // concreta que implica las dos cosas —haber llegado Y haber
        // renderizado—, y no depende de que el estado de carga se asiente.
        await paginaPortal.Locator("#password-actual")
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

        await paginaPortal.FillAsync("#password-actual", contrasenaTemporal);
        await paginaPortal.FillAsync("#password-nueva", contrasenaNueva);
        await paginaPortal.FillAsync("#password-confirmar", contrasenaNueva);
        await paginaPortal.ClickAsync("button[type=\"submit\"]");
        await paginaPortal.Locator(".nav-principal").WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

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
