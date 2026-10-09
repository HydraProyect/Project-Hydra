using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// Agrupar filas en un listado (línea «agrupar filas», pieza común sobre Centros): el desplegable «Agrupar»,
/// la cabecera de grupo que abre y contrae, «Expandir todo / Contraer todo» y la agrupación en la URL. Lo que
/// bUnit no ve y aquí sí: que el menú lo abre el circuito, que la navegación que escribe <c>agrupar</c> es real
/// y que recargar la dirección —con y sin el parámetro— reproduce la vista.
/// </summary>
[Collection("AppCollection")]
public class CentrosAgruparE2ETests(WebAppFixture fixture)
{
    // El campo se rotula como su pastilla de filtro: «Cliente» es el rótulo de pantalla del Cliente empresarial.
    private static readonly Regex AgrupadoPorCliente = new("^Agrupar: Cliente$");
    private static readonly Regex OpcionPorCliente = new("^Por Cliente$");
    private static readonly Regex SinAgrupar = new("^Agrupar: no$");

    private static ILocator Grupos(IPage page) => page.Locator(".grupo-lista");
    private static ILocator Filas(IPage page) => page.Locator(".lista-filas-acordeon .tarjeta-fila-acordeon");

    private async Task<IPage> AbrirCentrosAsync(IBrowserContext contexto, string consulta = "")
    {
        var page = await contexto.NewPageAsync();
        await page.SetViewportSizeAsync(1280, 800);
        // El Gestor CAE de la demo tiene Asignación de Cartera y Centros sembrados: «Agrupar» solo se pinta
        // con Centros en la página.
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailGestorRefrielectric, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/centros{consulta}");
        await Expect(Ayudas.DesplegableAgrupar(page)).ToBeVisibleAsync(new() { Timeout = 30_000 });
        return page;
    }

    /// <summary>
    /// Pulsa hasta que la condición se cumple: un control que llega con el prerender antes que el circuito
    /// pierde el clic (mismo motivo que <see cref="Ayudas.ElegirAgrupacionAsync"/>). Tras cada clic espera un
    /// segundo antes de volver a mirar, así que un conmutador ya atendido no recibe un segundo clic que lo deshaga.
    /// </summary>
    private static async Task PulsarHastaAsync(IPage page, ILocator control, Func<Task<bool>> hecho, string queSeEsperaba)
    {
        for (var intento = 1; !await hecho(); intento++)
        {
            Assert.True(intento <= 10, $"Tras 10 clics no ocurrió: {queSeEsperaba}.");
            await control.ClickAsync();
            await page.WaitForTimeoutAsync(1_000);
        }
    }

    [Fact]
    public async Task De_serie_llega_agrupada_y_la_cabecera_de_grupo_abre_y_contrae_sus_filas()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await AbrirCentrosAsync(contexto);

        await Expect(Ayudas.DesplegableAgrupar(page)).ToHaveTextAsync(AgrupadoPorCliente);
        await Expect(Grupos(page)).Not.ToHaveCountAsync(0);
        await Expect(Filas(page)).ToHaveCountAsync(0);
        await Expect(page.Locator("button.grupo-lista-cabecera[aria-expanded='true']")).ToHaveCountAsync(0);

        // Un grupo cualquiera, por su nombre (la cabecera es un botón cuyo nombre accesible empieza por él).
        var nombre = (await page.Locator(".grupo-lista-nombre").AllTextContentsAsync())[0].Trim();
        var cabecera = page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^" + Regex.Escape(nombre)) });

        await PulsarHastaAsync(page, cabecera, async () => await cabecera.GetAttributeAsync("aria-expanded") == "true",
            "la cabecera de grupo se abre");
        await Expect(Filas(page)).Not.ToHaveCountAsync(0);
        await Expect(page.Locator("button.grupo-lista-cabecera[aria-expanded='true']")).ToHaveCountAsync(1);

        await cabecera.ClickAsync();
        await Expect(cabecera).ToHaveAttributeAsync("aria-expanded", "false");
        await Expect(Filas(page)).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Expandir_todo_abre_todos_los_grupos_y_Contraer_todo_los_cierra()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await AbrirCentrosAsync(contexto);
        var cerrados = page.Locator("button.grupo-lista-cabecera[aria-expanded='false']");
        var abiertos = page.Locator("button.grupo-lista-cabecera[aria-expanded='true']");
        var expandir = page.GetByRole(AriaRole.Button, new() { Name = "Expandir todo", Exact = true });
        var contraer = page.GetByRole(AriaRole.Button, new() { Name = "Contraer todo", Exact = true });
        await Expect(cerrados).Not.ToHaveCountAsync(0);

        // «Expandir todo» pasa a decir «Contraer todo» al aplicarse: un segundo clic no lo deshace sin querer.
        await PulsarHastaAsync(page, expandir, async () => await contraer.IsVisibleAsync(), "«Expandir todo» se aplica");
        await Expect(cerrados).ToHaveCountAsync(0);
        await Expect(abiertos).Not.ToHaveCountAsync(0);
        await Expect(Filas(page)).Not.ToHaveCountAsync(0);

        await contraer.ClickAsync();
        await Expect(abiertos).ToHaveCountAsync(0);
        await Expect(Filas(page)).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task El_desplegable_quita_y_pone_la_agrupacion_y_la_direccion_la_reproduce_al_recargar()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await AbrirCentrosAsync(contexto);
        var agrupar = Ayudas.DesplegableAgrupar(page);
        await Expect(Grupos(page)).Not.ToHaveCountAsync(0);

        // --- «Sin agrupar»: sin cabeceras, todas las filas, y la dirección lo dice. ---
        await Ayudas.ElegirAgrupacionAsync(page, new Regex("^Sin agrupar$"), SinAgrupar);
        await Expect(Grupos(page)).ToHaveCountAsync(0);
        await Expect(Filas(page)).Not.ToHaveCountAsync(0);
        await Expect(page).ToHaveURLAsync(new Regex(@"[?&]agrupar=no(&|$)"));

        // --- Recargar esa dirección reproduce la vista sin agrupar. ---
        await page.ReloadAsync();
        await Expect(agrupar).ToHaveTextAsync(SinAgrupar, new() { Timeout = 30_000 });
        await Expect(Grupos(page)).ToHaveCountAsync(0);
        await Expect(Filas(page)).Not.ToHaveCountAsync(0);

        // --- «Por Cliente»: vuelve la vista de fábrica, que no deja rastro en la dirección. ---
        await Ayudas.ElegirAgrupacionAsync(page, OpcionPorCliente, AgrupadoPorCliente);
        await Expect(Grupos(page)).Not.ToHaveCountAsync(0);
        await Expect(page).Not.ToHaveURLAsync(new Regex("agrupar="));

        // --- Recargar la dirección sin el parámetro reproduce la vista agrupada. ---
        await page.ReloadAsync();
        await Expect(agrupar).ToHaveTextAsync(AgrupadoPorCliente, new() { Timeout = 30_000 });
        await Expect(Grupos(page)).Not.ToHaveCountAsync(0);
        await Expect(Filas(page)).ToHaveCountAsync(0);
    }

    /// <summary>Un enlace: <c>?agrupar=no</c> llega sin agrupar y la clave explícita llega agrupada.</summary>
    [Theory]
    [InlineData("?agrupar=no", false)]
    [InlineData("?agrupar=cliente", true)]
    public async Task Un_enlace_con_el_parametro_agrupar_abre_la_vista_que_dice(string consulta, bool agrupada)
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await AbrirCentrosAsync(contexto, consulta);

        await Expect(Ayudas.DesplegableAgrupar(page)).ToHaveTextAsync(agrupada ? AgrupadoPorCliente : SinAgrupar);
        if (agrupada)
        {
            await Expect(Grupos(page)).Not.ToHaveCountAsync(0);
            await Expect(Filas(page)).ToHaveCountAsync(0);
        }
        else
        {
            await Expect(Grupos(page)).ToHaveCountAsync(0);
            await Expect(Filas(page)).Not.ToHaveCountAsync(0);
        }
    }
}
