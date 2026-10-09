using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// Recorridos de fase 1. AppCollection conserva la serialización y la base efímera
/// de WebAppFixture. No se modifica SQL ni la identidad de runtime; los Vehículos se crean por UI.
/// Los botones de orden siguen siendo los de QuickGrid: se observan sus datos y clases actuales,
/// sin fijar qué dirección debe anunciar el nombre accesible del botón.
/// </summary>
[Collection("AppCollection")]
public class ListadosFase1VehiculosDocumentosTests(WebAppFixture fixture)
{
    [Fact]
    public async Task Vehiculos_las_opciones_nativas_conservan_el_orden_por_Modelo_en_ambos_sentidos()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/vehiculos");
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var prefijo = $"F1-{sufijo}";
        // Nombre y Modelo tienen órdenes opuestos; ignorar Modelo no puede pasar este caso.
        await CrearVehiculoAsync(page, $"{prefijo} Alfa", "Modelo Z", $"{prefijo}-A");
        await CrearVehiculoAsync(page, $"{prefijo} Beta", "Modelo A", $"{prefijo}-B");
        await page.GetByPlaceholder("Filtrar esta pantalla: nombre, modelo o matrícula").FillAsync(prefijo);
        var nombres = page.Locator("tbody .celda-vehiculo .enlace-nombre-fila");
        var modelos = page.Locator("tbody .celda-vehiculo-modelo");
        await Expect(nombres).ToHaveCountAsync(2);
        var cabecera = Cabecera(page, "Vehículo");
        await cabecera.Locator(".col-options-button").ClickAsync();
        await cabecera.Locator(".col-options select").FocusAsync();
        await cabecera.Locator(".col-options").GetByLabel("Ordenar por", new LocatorGetByLabelOptions { Exact = true })
            .SelectOptionAsync("Modelo");
        await cabecera.Locator(".col-options").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });
        await Expect(cabecera.Locator("button.col-title")).ToBeFocusedAsync();
        await OrdenAscendenteAsync(cabecera);
        await Expect(modelos).ToHaveTextAsync(["Modelo A", "Modelo Z"]);
        await Expect(nombres).ToHaveTextAsync([$"{prefijo} Beta", $"{prefijo} Alfa"]);
        await cabecera.Locator("button.col-title").ClickAsync();
        await Expect(modelos).ToHaveTextAsync(["Modelo Z", "Modelo A"]);
        await Expect(nombres).ToHaveTextAsync([$"{prefijo} Alfa", $"{prefijo} Beta"]);

        // El clic anterior deja foco real en el botón nativo; j debe sacarlo de ese
        // control hacia la primera fila ordenada, y Enter abrir ese Vehículo concreto.
        await Expect(cabecera.Locator("button.col-title")).ToBeFocusedAsync();
        var filas = page.Locator("tbody tr").Filter(new LocatorFilterOptions { Has = page.Locator(".celda-vehiculo") });
        await page.Keyboard.PressAsync("j");
        await Expect(filas.Nth(0)).ToHaveClassAsync(new Regex(@"\bfila-enfocada\b"));
        await Expect(filas.Nth(0)).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Enter");
        // La vista rápida es el panel del Context Workspace (la fila ya no lleva menú «⋯»).
        var panel = page.Locator(".workspace-panel");
        await Expect(panel.Locator(".workspace-titulo-entidad")).ToHaveTextAsync($"{prefijo} Alfa");
        await panel.Locator("button.workspace-cerrar").ClickAsync();
        await Expect(panel).ToHaveCountAsync(0);

        // El nombre de la segunda fila debe seguir abriendo Beta tras ordenar por Modelo.
        await filas.Nth(1).Locator(".nombre-abre-vista-rapida").ClickAsync();
        await Expect(panel.Locator(".workspace-titulo-entidad")).ToHaveTextAsync($"{prefijo} Beta");
    }

    [Fact]
    public async Task Documentos_las_opciones_nativas_ordenan_datos_sembrados_por_Ambito_Emision_y_Vencimiento()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/documentos");
        await page.Locator("table.tabla-datos").WaitForAsync();
        // Las dos direcciones hacen visibles los extremos de la unión, aunque una página
        // completa pueda contener solo Trabajadores. No se afirma un número fijo de documentos.
        var entidad = Cabecera(page, "Entidad asociada");
        await ElegirOpcionDocumentoAsync(entidad, "Ordenar por ámbito");
        await ComprobarOrdenDocumentoAsync(page, entidad, "ambito");

        // Vigente evita la ambigüedad de la colocación de fechas nulas al comprobar vencimiento.
        // La siembra tiene emisiones distintas y fechas de vencimiento diversas; los controles
        // posteriores fallan explícitamente si ese universo deja de distinguir ambos sentidos.
        // El estado se filtra en la franja de estado (sustituyó a la pastilla «Estado»): un botón por
        // estado, localizable por su estado de código (data-estado).
        var botonVigentes = page.Locator(".franja-estado-boton[data-estado='Vigente']");
        await botonVigentes.ClickAsync();
        await Expect(botonVigentes).ToHaveAttributeAsync("aria-pressed", "true");
        var vigencia = Cabecera(page, "Vigencia");
        await ElegirOpcionDocumentoAsync(vigencia, "Ordenar por emisión");
        await ComprobarOrdenDocumentoAsync(page, vigencia, "emision");
        await ElegirOpcionDocumentoAsync(vigencia, "Ordenar por vencimiento");
        await ComprobarOrdenDocumentoAsync(page, vigencia, "vencimiento");
    }

    [Fact]
    public async Task Documentos_Plantillas_se_reconstruye_desde_URL_y_conserva_una_primaria()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        var paginaFria = await contexto.NewPageAsync();
        await Ayudas.NavegarYEsperarAsync(paginaFria, $"{fixture.BaseUrl}/documentos?Pestana=plantillas");
        await Expect(paginaFria.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Plantillas", Exact = true }))
            .ToHaveAttributeAsync("aria-selected", "true");
        var nuevaPlantilla = paginaFria.GetByRole(AriaRole.Button,
            new PageGetByRoleOptions { Name = "+ Nueva plantilla", Exact = true });
        await Expect(nuevaPlantilla).ToBeVisibleAsync();
        await Expect(paginaFria.Locator(".contenedor-pagina button.boton-primario")).ToHaveCountAsync(1);
        var nuevoDocumento = paginaFria.Locator("header.cabecera-pagina").GetByRole(AriaRole.Button,
            new LocatorGetByRoleOptions { Name = "+ Nuevo documento", Exact = true });
        await Expect(nuevoDocumento).ToHaveClassAsync(new Regex(@"\bboton-secundario\b"));
    }

    private static ILocator Cabecera(IPage page, string prefijo) =>
        page.Locator("thead th").Filter(new LocatorFilterOptions { HasTextRegex = new Regex("^" + Regex.Escape(prefijo)) });

    private static async Task OrdenAscendenteAsync(ILocator cabecera)
    {
        if ((await cabecera.GetAttributeAsync("class") ?? "").Contains("col-sort-desc", StringComparison.Ordinal))
            await cabecera.Locator("button.col-title").ClickAsync();
        await Expect(cabecera).ToHaveClassAsync(new Regex(@"\bcol-sort-asc\b"));
    }

    private static async Task ElegirOpcionDocumentoAsync(ILocator cabecera, string opcion)
    {
        await cabecera.Locator(".col-options-button").ClickAsync();
        await cabecera.Locator(".col-options").GetByRole(AriaRole.Button,
            new LocatorGetByRoleOptions { Name = opcion, Exact = true }).ClickAsync();
        await cabecera.Locator(".col-options").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });
        await Expect(cabecera.Locator("button.col-title")).ToBeFocusedAsync();
        await OrdenAscendenteAsync(cabecera);
    }

    internal static async Task CrearVehiculoAsync(IPage page, string nombre, string modelo, string matricula)
    {
        await page.Locator("header.cabecera-pagina").GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "+ Nuevo vehículo", Exact = true }).ClickAsync();
        var drawer = page.Locator(".drawer-panel");
        await drawer.GetByLabel("Nombre", new LocatorGetByLabelOptions { Exact = true }).FillAsync(nombre);
        var selector = drawer.Locator("select");
        if (await selector.CountAsync() > 0)
            await selector.SelectOptionAsync(new SelectOptionValue { Index = 1 });
        await drawer.GetByLabel("Modelo", new LocatorGetByLabelOptions { Exact = true }).FillAsync(modelo);
        await drawer.GetByLabel("Matrícula", new LocatorGetByLabelOptions { Exact = true }).FillAsync(matricula);
        await drawer.Locator(".drawer-pie").GetByRole(AriaRole.Button,
            new LocatorGetByRoleOptions { Name = "Guardar", Exact = true }).ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });
    }

    private static async Task ComprobarOrdenDocumentoAsync(IPage page, ILocator cabecera, string campo)
    {
        await EsperarDatosOrdenadosAsync(page, campo, descendente: false);
        var ascendente = await LeerClavesDocumentoAsync(page, campo);
        Assert.True(ascendente.Length >= 2, "Control positivo: la página contiene datos comparables.");
        await cabecera.Locator("button.col-title").ClickAsync();
        await Expect(cabecera).ToHaveClassAsync(new Regex(@"\bcol-sort-desc\b"));
        // Exigir un extremo distinto evita leer la página anterior, homogénea, antes de la respuesta.
        await EsperarDatosOrdenadosAsync(page, campo, descendente: true, mayorQue: ascendente[0]);
        var descendente = await LeerClavesDocumentoAsync(page, campo);
        Assert.True(descendente[0] > ascendente[0],
            "El universo real debe distinguir los extremos ascendente y descendente; no basta un indicador.");
    }

    private const string ClavesDocumentoJs = """
        campo => {
          const ambitos = {'Trabajador':0,'Cliente':1,'Empresa':2,'Vehículo':3,'Proyecto':4};
          return [...document.querySelectorAll('tbody tr')].map(fila => {
            if (campo === 'ambito') {
              const nodo = fila.querySelector('.documentos-ambito');
              return nodo ? ambitos[nodo.textContent.trim()] : null;
            }
            const nodo = fila.querySelector('.documentos-vigencia');
            const fechas = nodo?.textContent.match(/\d{2}\/\d{2}\/\d{4}/g) || [];
            const fecha = fechas[campo === 'emision' ? 0 : 1];
            if (!fecha) return null;
            const [dia, mes, ano] = fecha.split('/').map(Number);
            return Date.UTC(ano, mes-1, dia);
          }).filter(clave => clave != null);
        }
        """;

    private static Task<double[]> LeerClavesDocumentoAsync(IPage page, string campo) =>
        page.EvaluateAsync<double[]>(ClavesDocumentoJs, campo);

    private static async Task EsperarDatosOrdenadosAsync(IPage page, string campo, bool descendente, double? mayorQue = null)
    {
        // La función espera datos, no duerme ni toma el cambio visual del indicador como prueba de orden.
        var funcion = "({campo, descendente, mayorQue}) => { const leer = " + ClavesDocumentoJs + "; " +
            "const claves = leer(campo); return claves.length >= 2 && " +
            "(mayorQue == null || claves[0] > mayorQue) && claves.every((x,i) => i === 0 || " +
            "(descendente ? claves[i-1] >= x : claves[i-1] <= x)); }";
        try
        {
            await page.WaitForFunctionAsync(funcion, new { campo, descendente, mayorQue },
                new PageWaitForFunctionOptions { Timeout = 15_000 });
        }
        catch (TimeoutException)
        {
            var claves = await LeerClavesDocumentoAsync(page, campo);
            Assert.True(claves.Length >= 2, $"Sin datos suficientes para verificar {campo}; no demuestra un orden incorrecto.");
            throw new Xunit.Sdk.XunitException(
                $"No se observó el orden {campo} {(descendente ? "descendente" : "ascendente")} esperado. " +
                $"Claves observadas: [{string.Join(", ", claves)}]; extremo anterior: {mayorQue}.");
        }
    }
}
