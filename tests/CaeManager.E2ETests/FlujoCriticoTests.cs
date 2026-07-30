using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Cubre el flujo de negocio crítico end-to-end, priorizado explícitamente
/// por el usuario sobre la cobertura módulo a módulo (ver ROADMAP.md,
/// "Iniciativa de hardening" § Tests E2E automatizados): login → crear
/// Cliente → crear Empresa asociada → crear Trabajador de esa Empresa →
/// subir un Documento suyo → el semáforo de vigencia muestra el estado
/// correcto. Un único test deliberadamente largo en vez de varios tests
/// pequeños con estado compartido — cada paso depende del anterior
/// (Empresa necesita el Cliente, Trabajador necesita la Empresa, Documento
/// necesita el Trabajador) y dividirlo solo añadiría fragilidad de orden de
/// ejecución sin ganar nada.
/// </summary>
[Collection("AppCollection")]
public class FlujoCriticoTests(WebAppFixture fixture)
{
    [Fact]
    public async Task Flujo_completo_login_cliente_empresa_trabajador_documento_semaforo()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var razonSocialCliente = $"Cliente E2E {sufijo}";
        var razonSocialEmpresa = $"Empresa E2E {sufijo}";
        var nombreTrabajador = "Trabajador";
        var apellidosTrabajador = $"E2E {sufijo}";
        var dniTrabajador = Ayudas.GenerarDniValido(88_000_001);

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);

        var drawer = page.Locator(".drawer-panel");

        // --- Crear Cliente ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes");

        // .First: el tenant del Administrador (Consultora, ver ADR-004 § 5.1)
        // no tiene datos operativos propios, así que la lista arranca vacía
        // y "+ Nuevo cliente" aparece tanto en la cabecera como en el
        // EstadoVacio — cualquiera de los dos abre el mismo drawer.
        await page.GetByText("+ Nuevo cliente").First.ClickAsync();
        await drawer.GetByLabel("Razón social").FillAsync(razonSocialCliente);
        await drawer.GetByLabel("CIF", new LocatorGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_999_901));
        await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        // --- Crear Empresa asociada al Cliente ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/empresas");
        await page.GetByText("+ Nueva empresa").First.ClickAsync();
        await drawer.GetByLabel("Razón social").FillAsync(razonSocialEmpresa);
        await drawer.GetByLabel("CIF", new LocatorGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_999_902));

        // El selector de Clientes es una lista de checkboxes con buscador (ver
        // SelectorMultiple.razor) — se filtra por el nombre exacto que
        // acabamos de crear en vez de buscar entre los 200 clientes sembrados.
        await drawer.GetByPlaceholder("Buscar…").FillAsync(razonSocialCliente);
        await drawer.GetByLabel(razonSocialCliente).CheckAsync();

        await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();

        // Tras crear una Empresa el drawer NO se cierra a propósito — pasa a
        // modo edición para dejar las credenciales de acceso visibles sin
        // reabrir el formulario (ver Empresas.razor.cs, GuardarAsync: "Tras
        // crear, el drawer no se cierra — pasa a modo edición..."). Se
        // confirma el guardado esperando el título "Editar empresa" y se
        // cierra explícitamente, en vez de esperar a que se oculte solo.
        await drawer.GetByText("Editar empresa").WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        await drawer.Locator(".drawer-cerrar").ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 5_000 });

        // --- Crear Trabajador de esa Empresa ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/trabajadores");
        await page.GetByText("+ Nuevo trabajador").First.ClickAsync();

        // El radio "Empresa" ya viene marcado por defecto (ver Trabajadores.razor.cs, _tipoEmpleador = "empresa").
        // GetByLabel("Empresa") es ambiguo dentro del drawer: además del
        // <select> de CampoSelect, el propio radio de "tipo de empleador"
        // también se llama "Empresa" — se desambigua por rol (combobox vs.
        // radio) en vez de por texto.
        await drawer.GetByRole(AriaRole.Combobox, new LocatorGetByRoleOptions { Name = "Empresa" })
            .SelectOptionAsync(new SelectOptionValue { Label = razonSocialEmpresa });
        await drawer.GetByLabel("Documento de identidad (DNI, NIE, TIE o pasaporte)").FillAsync(dniTrabajador);
        await drawer.GetByLabel("Nombre", new LocatorGetByLabelOptions { Exact = true }).FillAsync(nombreTrabajador);
        await drawer.GetByLabel("Apellidos").FillAsync(apellidosTrabajador);

        await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        // --- Subir Documento del Trabajador con vencimiento a 10 días (Urgente: umbral rojo = 15 días) ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/documentos");
        await page.GetByText("+ Nuevo documento").First.ClickAsync();

        // Ámbito por defecto ya es Trabajador (ver Documentos.razor.cs, _ambitoAplicacion).
        await drawer.GetByLabel("Trabajador", new LocatorGetByLabelOptions { Exact = true })
            .SelectOptionAsync(new SelectOptionValue { Label = $"{nombreTrabajador} {apellidosTrabajador} ({dniTrabajador})" });

        // "Formación 60h (base convenio)" no tiene vencimiento automático (ver
        // TipoDocumentoSeedData) — habilita el campo de fecha de vencimiento
        // manual, indispensable para fijar determinísticamente el estado del
        // semáforo en vez de depender del cálculo automático por meses.
        await drawer.GetByLabel("Tipo de documento").SelectOptionAsync(new SelectOptionValue { Label = "Formación 60h (base convenio)" });

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        await drawer.GetByLabel("Fecha de emisión").FillAsync(hoy.ToString("yyyy-MM-dd"));
        await drawer.GetByLabel("Fecha de vencimiento").FillAsync(hoy.AddDays(10).ToString("yyyy-MM-dd"));

        var rutaPdf = Ayudas.GenerarPdfDePruebaEnDisco();
        try
        {
            await drawer.Locator("input[type=\"file\"]").SetInputFilesAsync(rutaPdf);
            await drawer.GetByText("Archivo adjuntado correctamente.").WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

            await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
            await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });
        }
        finally
        {
            File.Delete(rutaPdf);
        }

        // --- El semáforo de vigencia muestra "Urgente" para este Documento ---
        // La búsqueda del grid filtra por PropietarioNombre/TipoDocumentoNombre,
        // no por DNI (ver ObtenerDocumentosQueryHandler) — se busca por los
        // apellidos únicos del trabajador de prueba. No hace falta esperar el
        // debounce de CampoTexto (300ms) a mano: el WaitForAsync de más abajo
        // reintenta hasta encontrar la fila, que solo aparece una vez que el
        // grid recibe la búsqueda ya filtrada.
        await page.GetByPlaceholder("Buscar por propietario o tipo de documento…").FillAsync(apellidosTrabajador);

        var fila = page.Locator("tr", new PageLocatorOptions { HasText = apellidosTrabajador });
        await fila.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        var insigniaEstado = fila.Locator(".badge-peligro");
        await insigniaEstado.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        Assert.Equal("Urgente", (await insigniaEstado.InnerTextAsync()).Trim());
    }
}
