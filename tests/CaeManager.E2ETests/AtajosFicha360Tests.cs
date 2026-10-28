using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Capa de teclado de las fichas 360 (<c>atajos-ficha.js</c>) sobre una ficha real, Subcontrata
/// 360: no hay componente ni C# de por medio, así que solo un navegador puede probar que las
/// teclas llegan al control que el usuario tiene delante. Un único recorrido encadenado:
/// 1–9 cambian de pestaña (la 3 es Plataformas, la de las filas con botón), j/k recorren las filas, Alt enseña las pistas (las cifras sobre las
/// pestañas y E sobre el botón de la fila enfocada), «e» pulsa ese botón, y con el diálogo que
/// abre ninguna tecla de ficha actúa.
/// </summary>
/// <remarks>
/// Subcontrata 360 porque su lista de pestañas es hermana del cuerpo de la ficha (no lo envuelve
/// ni vive dentro): es la colocación que una búsqueda por parentesco no encontraba.
/// </remarks>
[Collection("AppCollection")]
public class AtajosFicha360Tests(WebAppFixture fixture)
{
    [Fact]
    public async Task Las_teclas_de_ficha_cambian_de_pestana_recorren_filas_y_pulsan_la_accion_de_la_fila_en_Subcontrata_360()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        // El Administrador, como los E2E que dan de alta Cliente, Centro y Subcontrata por la interfaz
        // (AltaGuiadaTests, F3bSubcontrataSmokeTests): ve sin alcance de cartera lo que el propio test crea.
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);
        await PrepararFichaConDocumentosPorSupervisarAsync(page);

        var html = page.Locator("html");
        var enfocada = page.Locator("[data-fila-enfocada]");
        var dialogo = page.Locator("[role='dialog'][aria-modal='true']");

        await Assertions.Expect(page.Locator("[data-atajos-ficha]")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        // La ficha creada por el test debe tener un documento por supervisar: sin fila con botón no se puede probar «e».
        // La pestaña carga su lista por el circuito: se espera a que pinte la fila.
        await EsperarAsync(page, $"{PestanaActiva} === 2 && {FilaConBoton} >= 0");

        // --- 1–9 cambian de pestaña (la lista de pestañas es hermana del cuerpo de la ficha). ---
        await page.Keyboard.PressAsync("1");
        await EsperarAsync(page, $"{PestanaActiva} === 0");
        await page.Keyboard.PressAsync("3");
        await EsperarAsync(page, $"{PestanaActiva} === 2");
        await EsperarAsync(page, $"{FilaConBoton} >= 0");

        // --- j/k recorren las filas; sin fila enfocada, la primera. La fila recibe el foco real. ---
        await Assertions.Expect(enfocada).ToHaveCountAsync(0);
        await page.Keyboard.PressAsync("j");
        await EsperarAsync(page, $"{FilaEnfocada} === 0");
        await Ayudas.EsperarFocoRealEnFilaAsync(enfocada);
        await page.Keyboard.PressAsync("j");
        await EsperarAsync(page, $"{FilaEnfocada} === 1");
        await Ayudas.EsperarFocoRealEnFilaAsync(enfocada);
        await page.Keyboard.PressAsync("k");
        await EsperarAsync(page, $"{FilaEnfocada} === 0");
        await Ayudas.EsperarFocoRealEnFilaAsync(enfocada);

        // Hasta la primera fila con botón.
        var indiceConBoton = await page.EvaluateAsync<int>($"() => {FilaConBoton}");
        for (var i = 0; i < indiceConBoton; i++)
        {
            await page.Keyboard.PressAsync("j");
            await EsperarAsync(page, $"{FilaEnfocada} === {i + 1}");
            await Ayudas.EsperarFocoRealEnFilaAsync(enfocada);
        }
        await EsperarAsync(page, $"{FilaEnfocada} === {indiceConBoton}");

        // --- Alt pulsada y soltada, como en los listados: cifras sobre las pestañas y E sobre
        //     el botón de la fila enfocada. Esc apaga. ---
        await page.Keyboard.PressAsync("Alt");
        await Assertions.Expect(html).ToHaveAttributeAsync("data-keytips", "raiz");
        var pintadas = (await page.Locator(".keytip").AllTextContentsAsync()).Select(l => l.Trim()).ToList();
        foreach (var pista in new[] { "1", "2", "3", "E" })
            Assert.Single(pintadas, l => l == pista);
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(page.Locator(".keytip")).ToHaveCountAsync(0);

        // --- «e» pulsa el botón de la fila enfocada: abre su diálogo. ---
        await Assertions.Expect(dialogo).ToHaveCountAsync(0);
        await page.Keyboard.PressAsync("e");
        await Assertions.Expect(dialogo).ToBeVisibleAsync();

        // --- Con el diálogo abierto las teclas de ficha no actúan: ni cambia la pestaña ni se
        //     mueve la fila. El control positivo es que antes sí lo hacían. ---
        await page.Keyboard.PressAsync("1");
        await page.Keyboard.PressAsync("k");
        await Assertions.Expect(dialogo).ToBeVisibleAsync();
        Assert.Equal(2, await page.EvaluateAsync<int>($"() => {PestanaActiva}"));
        Assert.Equal(indiceConBoton, await page.EvaluateAsync<int>($"() => {FilaEnfocada}"));
    }

    private const string Filas = "[...document.querySelectorAll(\"[data-atajos-ficha] .cuerpo-con-lateral-principal [data-pieza='fila']\")]";
    private const string PestanaActiva = "[...document.querySelectorAll('[role=tab]')].findIndex(t => t.getAttribute('aria-selected') === 'true')";
    private const string FilaEnfocada = Filas + ".findIndex(f => f.hasAttribute('data-fila-enfocada'))";
    private const string FilaConBoton = Filas + ".findIndex(f => f.querySelector('.fila-relacion-acciones button:not(.boton-360)'))";

    private async Task PrepararFichaConDocumentosPorSupervisarAsync(IPage page)
    {
        var sufijo = Guid.NewGuid().ToString("N");
        var empresa = $"Atajos Empresa {sufijo}";
        var clienteEmpresarial = $"Atajos Cliente {sufijo}";
        var centro = $"Atajos Centro {sufijo}";
        var subcontrata = $"Atajos Subcontrata {sufijo}";
        var drawer = page.Locator(".drawer-panel");
        // Tres CIF distintos y propios de esta ejecución, por encima de las siembras (7_000_000, 7_500_000) y
        // por debajo de las constantes fijas (9_99x_xxx) de los demás E2E.
        var semillaCif = Random.Shared.Next(9_000_000, 9_500_000);

        // Mismo circuito de alta que AltaGuiadaTests, en el Tenant del Administrador (el que comparten los E2E).
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes/alta-guiada");
        await page.GetByRole(AriaRole.Heading, new() { Name = "1. Empresa" }).WaitForAsync();
        await page.GetByLabel("Razón social").FillAsync(empresa);
        // La identificación fiscal es obligatoria (etiqueta con asterisco), como en AltaGuiadaTests.
        await page.GetByLabel("Identificación fiscal *", new() { Exact = true })
            .FillAsync(Ayudas.GenerarCifValido(semillaCif));
        await page.GetByRole(AriaRole.Button, new() { Name = "Guardar y continuar", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "2. Cliente" }).WaitForAsync();
        await page.GetByLabel("Razón social").FillAsync(clienteEmpresarial);
        await page.GetByLabel("Identificación fiscal *", new() { Exact = true })
            .FillAsync(Ayudas.GenerarCifValido(semillaCif + 1));
        await page.GetByRole(AriaRole.Button, new() { Name = "Guardar y continuar a Centro", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "3. Centro" }).WaitForAsync();
        await page.GetByLabel("Nombre", new() { Exact = true }).FillAsync(centro);
        await page.GetByRole(AriaRole.Button, new() { Name = "Guardar centro", Exact = true }).ClickAsync();
        // «Terminar aquí» solo se pinta con cero Centros creados: que desaparezca es la señal de que el Centro se guardó.
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Terminar aquí", Exact = true }))
            .Not.ToBeVisibleAsync(new() { Timeout = 15_000 });

        // Alta por el formulario de F3bSubcontrataSmokeTests, vinculada solo al Cliente empresarial propio.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/subcontratas");
        await page.GetByRole(AriaRole.Button, new() { Name = "+ Nueva subcontrata", Exact = true }).ClickAsync();
        await drawer.GetByLabel("Razón social").FillAsync(subcontrata);
        await drawer.GetByLabel("Identificación fiscal *", new() { Exact = true })
            .FillAsync(Ayudas.GenerarCifValido(semillaCif + 2));
        await MarcarCasillaEnSelectorMultipleAsync(drawer, "Clientes que la contrataron", clienteEmpresarial);
        await drawer.Locator(".drawer-pie").GetByRole(AriaRole.Button, new() { Name = "Guardar", Exact = true }).ClickAsync();
        await drawer.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        // La ficha 360 de la Subcontrata recién creada: su icono 360 es el único enlace cuyo nombre lleva la razón social.
        await page.GetByPlaceholder("Filtrar esta pantalla: razón social o CIF").FillAsync(subcontrata);
        var enlace360 = page.GetByRole(AriaRole.Link, new() { Name = subcontrata });
        await enlace360.WaitForAsync(new() { Timeout = 15_000 });
        var ruta = await enlace360.GetAttributeAsync("href");
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}{ruta}?pestana=plataformas");

        // Los botones de la cabecera de la ficha: «Registrar verificación» también está en la pestaña Plataformas.
        // El menú llega con el prerender antes que el circuito: PulsarAccionDeMenuAsync reabre y reintenta (DeepLinksTests).
        var cabecera = page.Locator(".cabecera-identidad-acciones");
        await Ayudas.PulsarAccionDeMenuAsync(cabecera.Locator(".menu-acciones-disparador"), "Cambiar a Supervisada");

        // RegistrarVerificacionExterna por la aplicación, sin SQL ni cambios al catálogo compartido.
        // Dos verificaciones NoEncontrado garantizan filas pendientes para el recorrido j/j/k.
        var rutaPdf = Ayudas.GenerarPdfDePruebaEnDisco();
        try
        {
            string[] tipos = [];
            for (var i = 0; i < 2; i++)
            {
                await cabecera.GetByRole(AriaRole.Button, new() { Name = "Registrar verificación", Exact = true }).ClickAsync();
                var selectorTipo = drawer.GetByLabel("Tipo de documento", new() { Exact = true });
                // El formulario carga los tipos antes de abrirse: con el selector en pantalla ya están sus opciones.
                await selectorTipo.WaitForAsync();
                if (i == 0)
                    tipos = await selectorTipo.Locator("option[value]:not([value=''])").EvaluateAllAsync<string[]>(
                        "opciones => opciones.slice(0, 2).map(o => o.value)");
                await drawer.GetByLabel("Centro", new() { Exact = true })
                    .SelectOptionAsync(new SelectOptionValue { Label = $"{centro} ({clienteEmpresarial})" });
                await selectorTipo.SelectOptionAsync(tipos[i]);
                await drawer.GetByLabel("Resultado", new() { Exact = true }).SelectOptionAsync("NoEncontrado");
                await drawer.GetByLabel("Fecha de verificación", new() { Exact = true })
                    .FillAsync(Ayudas.HoyDeNegocio().ToString("yyyy-MM-dd"));
                await drawer.Locator("input[type='file']").SetInputFilesAsync(rutaPdf);
                // La evidencia se lee a memoria por el circuito: el formulario enseña su nombre cuando ya llegó.
                await Assertions.Expect(drawer).ToContainTextAsync($"Adjunto: {Path.GetFileName(rutaPdf)}");
                await drawer.Locator(".drawer-pie").GetByRole(AriaRole.Button, new() { Name = "Guardar verificación", Exact = true }).ClickAsync();
                await drawer.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 15_000 });
            }
        }
        finally
        {
            File.Delete(rutaPdf);
        }

        // Empezar los atajos sin el foco ni el estado de teclado de la preparación.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}{ruta}?pestana=plataformas");
    }

    /// <summary>
    /// El drawer de alta de Subcontrata tiene DOS selectores múltiples y un Cliente recién creado (que también es una
    /// Empresa) aparece en ambos: se acota al grupo (<c>role="group"</c> con la etiqueta como nombre) del selector.
    /// Mismo propósito que el ayudante de VinculacionUsuarioClienteRelacionEmpresarialE2ETests, por rol y no por texto.
    /// </summary>
    private static async Task MarcarCasillaEnSelectorMultipleAsync(ILocator drawer, string etiquetaSelector, string nombreElemento)
    {
        var selector = drawer.GetByRole(AriaRole.Group, new() { Name = etiquetaSelector, Exact = true });

        await selector.GetByPlaceholder("Buscar…").FillAsync(nombreElemento);

        var casilla = selector.GetByRole(AriaRole.Checkbox, new() { Name = nombreElemento });
        await Assertions.Expect(casilla).ToHaveCountAsync(1, new() { Timeout = 10_000 });
        await casilla.CheckAsync();
    }

    private static Task EsperarAsync(IPage page, string condicion, float tiempoMs = 15_000) =>
        page.WaitForFunctionAsync($"() => {condicion}", null, new() { Timeout = tiempoMs });
}
