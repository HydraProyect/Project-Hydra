using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

[Collection("AppCollection")]
public class VegaPanelE2ETests(WebAppFixture fixture)
{
    [Fact]
    public async Task La_consulta_del_panel_llega_al_doble_con_el_DNI_enmascarado()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);

        await AbrirAsync(page);
        var panel = Panel(page);
        await panel.GetByRole(AriaRole.Textbox, new() { Name = "Mensaje para Vega" })
            .FillAsync("¿Qué formación PRL necesita la persona con DNI 12345678Z?");
        await panel.GetByRole(AriaRole.Button, new() { Name = "Enviar", Exact = true }).ClickAsync();
        await Expect(panel).ToContainTextAsync("Respuesta del doble de Vega. Historial recibido:");

        const string prefijo = "Historial recibido: ";
        var texto = await panel.InnerTextAsync();
        var base64 = texto[(texto.IndexOf(prefijo, StringComparison.Ordinal) + prefijo.Length)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        var recibido = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        using var historial = JsonDocument.Parse(recibido);
        Assert.Equal(1, historial.RootElement.GetArrayLength());
        Assert.Equal("¿Qué formación PRL necesita la persona con DNI [DOC_1]?", historial.RootElement[0].GetProperty("Texto").GetString());
        Assert.DoesNotContain("12345678Z", recibido);
        await PlegarAsync(page);
    }

    /// <summary>
    /// El borrador vive en el circuito (AsistenteIa.razor.cs), no en el navegador: sobrevive a plegar y
    /// abrir, y el cambio de Tenant activo, que recarga la página y abre un circuito nuevo, no lo arrastra.
    /// Lo que fija la segunda mitad es que nada (vega.js, sessionStorage, localStorage) lo persiste
    /// fuera del circuito; no prueba el cambio de Tenant efectivo dentro de un mismo circuito.
    /// </summary>
    [Fact]
    public async Task El_borrador_se_conserva_al_plegar_y_no_sobrevive_al_cambio_de_Tenant_activo()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministradorConsultora, Ayudas.ContrasenaUsuariosPrueba);
        var tenantOrigen = await Ayudas.TenantActivoIdAsync(page);
        const string borrador = "Borrador privado del Tenant propietario de origen 12345678Z";
        await AbrirAsync(page);
        await Campo(page).FillAsync(borrador);
        await PlegarAsync(page);
        await AbrirAsync(page);
        await Expect(Campo(page)).ToHaveValueAsync(borrador);
        await PlegarAsync(page);

        await Ayudas.CambiarClienteActivoAsync(page, fixture.BaseUrl, Ayudas.NombreClienteDelegadoDemo);
        Assert.NotEqual(tenantOrigen, await Ayudas.TenantActivoIdAsync(page));
        await AbrirAsync(page);
        await Expect(Campo(page)).ToHaveValueAsync("");
        await Expect(Panel(page)).Not.ToContainTextAsync(borrador);
        await PlegarAsync(page);
    }

    private static ILocator Panel(IPage page) => page.GetByRole(AriaRole.Complementary, new() { Name = "Vega", Exact = true });
    private static ILocator Campo(IPage page) => Panel(page).GetByRole(AriaRole.Textbox, new() { Name = "Mensaje para Vega" });

    /// <summary>
    /// El botón es un islote interactivo: un clic antes de que el circuito conecte no hace nada, así que
    /// se repite hasta que el panel aparece (mismo criterio que Ayudas.AbrirSelectorTenantAsync).
    /// </summary>
    private static async Task AbrirAsync(IPage page)
    {
        var boton = page.GetByRole(AriaRole.Button, new() { NameRegex = new("^Abrir Vega, el asistente de ") });
        var panel = Panel(page);
        for (var intento = 0; intento < 20 && !await panel.IsVisibleAsync(); intento++)
        {
            await boton.ClickAsync();
            try
            {
                await panel.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 1_500 });
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                // El circuito aún no había conectado: se vuelve a pulsar.
            }
        }

        await Expect(panel).ToBeVisibleAsync();
    }

    private static async Task PlegarAsync(IPage page)
    {
        await Panel(page).GetByRole(AriaRole.Button, new() { Name = "Plegar (Ctrl+I)", Exact = true }).ClickAsync();
        await Expect(Panel(page)).ToBeHiddenAsync();
    }
}
