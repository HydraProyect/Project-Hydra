using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// Agrupar filas en un listado (pieza común sobre Centros). Aquí solo lo que necesita navegador: que el
/// desplegable «Agrupar» lo abre el circuito, que la navegación que escribe <c>agrupar</c> es real y que
/// recargar la dirección —con y sin el parámetro— reproduce la vista. Cómo se lee cada valor del parámetro,
/// el título de grupo y «Expandir todo» están en bUnit (<c>CentrosListaPatronTests</c>).
/// </summary>
[Collection("AppCollection")]
public class CentrosAgruparE2ETests(WebAppFixture fixture)
{
    // «Por Cliente» es la única agrupación; sin elegirla la lista nace sin agrupar y su pastilla dice solo «Agrupar».
    private static readonly Regex AgrupadoPorCliente = new("^Agrupar: Por Cliente$");
    private static readonly Regex SinAgrupar = new("^Agrupar$");

    private static ILocator Grupos(IPage page) => page.Locator(".cabecera-grupo-centros");
    private static ILocator Filas(IPage page) => page.Locator(".lista-filas-acordeon .tarjeta-fila-acordeon");

    private async Task<IPage> AbrirCentrosAsync(IBrowserContext contexto)
    {
        var page = await contexto.NewPageAsync();
        await page.SetViewportSizeAsync(1280, 800);
        // El Gestor CAE de la demo tiene Asignación de Cartera y Centros sembrados: «Agrupar» solo se pinta
        // con Centros en la página.
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailGestorRefrielectric, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/centros");
        await Expect(Ayudas.DesplegableAgrupar(page)).ToBeVisibleAsync(new() { Timeout = 30_000 });
        return page;
    }

    [Fact]
    public async Task El_desplegable_pone_y_quita_la_agrupacion_y_la_direccion_la_reproduce_al_recargar()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await AbrirCentrosAsync(contexto);
        var agrupar = Ayudas.DesplegableAgrupar(page);

        // --- De serie, sin agrupar: sin títulos de grupo y con todas las filas. ---
        await Ayudas.MostrarCentrosSinAgruparAsync(page);
        await Expect(Filas(page)).Not.ToHaveCountAsync(0);

        // --- «Por Cliente»: títulos de grupo sin contraer, las filas siguen a la vista y la dirección lo dice. ---
        await Ayudas.ElegirAgrupacionAsync(page, "Por Cliente", AgrupadoPorCliente);
        await Expect(Grupos(page)).Not.ToHaveCountAsync(0);
        await Expect(Filas(page)).Not.ToHaveCountAsync(0);
        await Expect(page).ToHaveURLAsync(new Regex(@"[?&]agrupar=cliente(&|$)"));

        // --- Recargar esa dirección reproduce la vista agrupada. ---
        await page.ReloadAsync();
        await Expect(agrupar).ToHaveTextAsync(AgrupadoPorCliente, new() { Timeout = 30_000 });
        await Expect(Grupos(page)).Not.ToHaveCountAsync(0);
        await Expect(Filas(page)).Not.ToHaveCountAsync(0);

        // --- «Sin agrupar»: vuelve la vista de fábrica, que no deja rastro en la dirección. ---
        await Ayudas.ElegirAgrupacionAsync(page, "Sin agrupar", SinAgrupar);
        await Expect(Grupos(page)).ToHaveCountAsync(0);
        await Expect(page).Not.ToHaveURLAsync(new Regex("agrupar="));

        // --- Recargar la dirección sin el parámetro reproduce la vista sin agrupar. ---
        await page.ReloadAsync();
        await Expect(agrupar).ToHaveTextAsync(SinAgrupar, new() { Timeout = 30_000 });
        await Expect(Grupos(page)).ToHaveCountAsync(0);
        await Expect(Filas(page)).Not.ToHaveCountAsync(0);
    }
}