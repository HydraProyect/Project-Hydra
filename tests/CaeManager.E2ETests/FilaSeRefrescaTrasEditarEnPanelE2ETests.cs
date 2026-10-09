using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// Tras guardar desde el lápiz de la vista rápida (Context Workspace), la fila del listado
/// enseña el dato nuevo sin recargar la página. El panel vive en MainLayout y guarda sin pasar
/// por la página del listado: avisa por <c>ContextWorkspaceService.OnEntidadGuardada</c> y la
/// página vuelve a pedir solo esa fila y la sustituye en sitio. Aquí se recorre entero, en un
/// navegador real, en los dos listados donde se declaró el hueco (Trabajadores y Vehículos);
/// la sustitución en sitio de los demás la prueban sus tests de bUnit.
/// </summary>
[Collection("AppCollection")]
public class FilaSeRefrescaTrasEditarEnPanelE2ETests(WebAppFixture fixture)
{
    private static readonly LocatorGetByLabelOptions Exacto = new() { Exact = true };

    private async Task<IPage> AbrirAsync(IBrowserContext contexto, string ruta)
    {
        var page = await contexto.NewPageAsync();
        await page.SetViewportSizeAsync(1280, 800);
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/{ruta}");
        return page;
    }

    /// <summary>Lápiz de la cabecera del panel → campo «Nombre» → Guardar.</summary>
    private static async Task GuardarNombreEnElPanelAsync(ILocator panel, string lapiz, string nombre)
    {
        await panel.Locator(lapiz).ClickAsync();
        await panel.GetByLabel("Nombre", Exacto).FillAsync(nombre);
        await panel.Locator(".workspace-acciones-edicion").GetByText("Guardar", new LocatorGetByTextOptions { Exact = true }).ClickAsync();
        // El panel vuelve a la lectura: el lápiz reaparece cuando el guardado terminó.
        await Expect(panel.Locator(lapiz)).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Trabajadores_la_fila_ensena_el_nombre_guardado_en_el_panel_sin_recargar()
    {
        const string lapiz = "button[aria-label='Editar información del trabajador']";
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await AbrirAsync(contexto, "trabajadores");
        var filas = page.Locator("tbody tr.fila-pulsable");
        await Expect(filas).Not.ToHaveCountAsync(0);

        // Se aísla un trabajador sembrado por su DNI, que la edición no toca: la fila sigue
        // cumpliendo el filtro y ningún localizador depende de qué más haya en la lista.
        var dni = (await filas.First.Locator(".boton-copiar-en-linea").InnerTextAsync()).Trim();
        var filtro = page.GetByPlaceholder("Filtrar esta pantalla: nombre, DNI o alias");
        await filtro.FillAsync(dni);
        await Expect(filas).ToHaveCountAsync(1);
        var nombreEnFila = filas.First.Locator(".nombre-abre-vista-rapida");
        var nombreCompletoOriginal = (await nombreEnFila.InnerTextAsync()).Trim();

        await nombreEnFila.ClickAsync();
        var panel = page.Locator(".workspace-panel");
        await Expect(panel.Locator(".workspace-titulo-entidad")).ToHaveTextAsync(nombreCompletoOriginal);
        await panel.Locator(lapiz).ClickAsync();
        var nombreOriginal = await panel.GetByLabel("Nombre", Exacto).InputValueAsync();
        await panel.Locator(".workspace-acciones-edicion").GetByText("Cancelar", new LocatorGetByTextOptions { Exact = true }).ClickAsync();

        var nombreEditado = $"{nombreOriginal} E2E{Guid.NewGuid().ToString("N")[..6]}";
        var restaurado = false;
        try
        {
            await GuardarNombreEnElPanelAsync(panel, lapiz, nombreEditado);

            // La fila cambia sola, sin recargar ni tocar la lista…
            await Expect(nombreEnFila).ToContainTextAsync(nombreEditado);
            // …y lo demás sigue como estaba: el filtro, la única fila y el panel sobre la misma ficha.
            await Expect(filtro).ToHaveValueAsync(dni);
            await Expect(filas).ToHaveCountAsync(1);
            await Expect(panel.Locator(".workspace-titulo-entidad")).ToContainTextAsync(nombreEditado);

            // De vuelta al nombre sembrado: segunda edición sobre el mismo panel, mismo refresco.
            await GuardarNombreEnElPanelAsync(panel, lapiz, nombreOriginal);
            await Expect(nombreEnFila).ToHaveTextAsync(nombreCompletoOriginal);
            restaurado = true;
        }
        finally
        {
            // La siembra es compartida por la colección: si algo falló a medias, se intenta
            // dejar el nombre como estaba sin tapar el fallo original.
            if (!restaurado)
            {
                try
                {
                    await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/trabajadores");
                    await page.GetByPlaceholder("Filtrar esta pantalla: nombre, DNI o alias").FillAsync(dni);
                    await page.Locator("tbody tr.fila-pulsable .nombre-abre-vista-rapida").First.ClickAsync();
                    await GuardarNombreEnElPanelAsync(page.Locator(".workspace-panel"), lapiz, nombreOriginal);
                }
                catch (Exception)
                {
                    // Mejor esfuerzo.
                }
            }
        }
    }

    [Fact]
    public async Task Vehiculos_la_fila_ensena_el_nombre_guardado_en_el_panel_y_conserva_el_filtro_y_la_fila_enfocada()
    {
        const string lapiz = "button[aria-label='Editar información del vehículo']";
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await AbrirAsync(contexto, "vehiculos");

        // Un vehículo propio del test, filtrado por su prefijo: una sola fila, sin depender de la siembra.
        var prefijo = $"FRP-{Guid.NewGuid().ToString("N")[..8]}";
        var nombre = $"{prefijo} Furgoneta";
        await ListadosFase1VehiculosDocumentosTests.CrearVehiculoAsync(page, nombre, "Modelo de prueba", $"{prefijo}-M");
        var filtro = page.GetByPlaceholder("Filtrar esta pantalla: nombre, modelo o matrícula");
        await filtro.FillAsync(prefijo);
        var fila = page.Locator("tbody tr").Filter(new LocatorFilterOptions { Has = page.Locator(".celda-vehiculo") });
        await Expect(fila).ToHaveCountAsync(1);
        var nombreEnFila = fila.Locator(".nombre-abre-vista-rapida");
        await Expect(nombreEnFila).ToHaveTextAsync(nombre);

        await EnfocarLaFilaConJAsync(page);
        await page.Keyboard.PressAsync("Enter");
        var panel = page.Locator(".workspace-panel");
        await Expect(panel.Locator(".workspace-titulo-entidad")).ToHaveTextAsync(nombre);

        var nombreEditado = $"{prefijo} Camión grúa";
        await GuardarNombreEnElPanelAsync(panel, lapiz, nombreEditado);

        // La fila cambia sola…
        await Expect(nombreEnFila).ToHaveTextAsync(nombreEditado);
        // …sin perder el filtro, la fila enfocada ni el panel, que sigue sobre la misma ficha.
        await Expect(filtro).ToHaveValueAsync(prefijo);
        await Expect(fila).ToHaveCountAsync(1);
        await Expect(page.Locator("tbody tr.fila-enfocada")).ToHaveCountAsync(1);
        await Expect(panel.Locator(".workspace-titulo-entidad")).ToHaveTextAsync(nombreEditado);
    }

    /// <summary>
    /// «j» enfoca la única fila. Se reintenta porque QuickGrid vuelve a pedir la página por su
    /// cuenta cuando el total cambia (aquí, al filtrar), y cada carga limpia la fila enfocada
    /// (misma espera que <c>VehiculosFilaSinMenuE2ETests</c>). El foco se saca antes del campo
    /// de filtro, donde «j» sería texto.
    /// </summary>
    private static async Task EnfocarLaFilaConJAsync(IPage page)
    {
        var enfocada = page.Locator("tbody tr.fila-enfocada");
        for (var intento = 0; intento < 5; intento++)
        {
            if (await enfocada.CountAsync() == 0)
            {
                await page.Locator("thead th.col-estado button.col-title").FocusAsync();
                await page.Keyboard.PressAsync("j");
            }

            await page.WaitForTimeoutAsync(400);
            if (await enfocada.CountAsync() == 1)
            {
                await page.WaitForTimeoutAsync(600);
                if (await enfocada.CountAsync() == 1) return;
            }
        }

        await Expect(enfocada).ToHaveCountAsync(1);
    }
}
