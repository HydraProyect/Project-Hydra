using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// Los filtros de un listado viajan en la URL (decisión D4 del 2026-10-08):
/// recargar o compartir el enlace reproduce la vista, y «Limpiar todo» los
/// quita también de la dirección. Sin esto último la siguiente pasada de
/// parámetros los repone desde la URL y el filtro «vuelve solo».
///
/// <para>
/// El recorrido usa el filtro de Empresa de Trabajadores, que antes de este
/// incremento vivía solo en memoria. No depende de qué Empresas siembra el
/// fixture: lee de la pastilla el nombre de una Empresa real, la elige por ese
/// nombre accesible y lee su Id de la URL.
/// </para>
/// </summary>
[Collection("AppCollection")]
public partial class FiltrosEnLaUrlTests(WebAppFixture fixture)
{
    [GeneratedRegex(@"[?&]empresa=[0-9a-fA-F-]{36}(&|$)")]
    private static partial Regex EmpresaEnLaUrl();

    [Fact]
    public async Task Trabajadores_el_filtro_de_Empresa_va_a_la_URL_sobrevive_a_una_carga_en_frio_y_Limpiar_todo_lo_quita()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/trabajadores");
        await page.Locator("table.tabla-datos").WaitForAsync();

        var pastillas = page.Locator(".barra-filtros-pastillas");
        await pastillas.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Empresa", Exact = true }).ClickAsync();
        var opciones = pastillas.GetByRole(AriaRole.Menuitemradio);
        await Expect(opciones).Not.ToHaveCountAsync(0);
        // «Todas» no filtra: se elige, por su nombre accesible, una Empresa real.
        var nombreEmpresa = (await opciones.AllInnerTextsAsync()).Select(t => t.Trim())
            .FirstOrDefault(t => t.Length > 0 && t != "Todas" && !t.StartsWith("Subcontrata", StringComparison.Ordinal));
        Assert.False(nombreEmpresa is null, "la siembra debe ofrecer al menos una Empresa en la pastilla");
        await pastillas.GetByRole(AriaRole.Menuitemradio, new LocatorGetByRoleOptions { Name = nombreEmpresa, Exact = true }).ClickAsync();

        await page.WaitForURLAsync(EmpresaEnLaUrl(), new PageWaitForURLOptions { Timeout = 30_000 });
        var urlConFiltro = page.Url;

        // Carga en frío en otra pestaña con esa URL exacta: reproduce compartir el
        // enlace o pulsar F5, no una navegación que reutilice el circuito abierto.
        var paginaFria = await contexto.NewPageAsync();
        await Ayudas.NavegarYEsperarAsync(paginaFria, urlConFiltro);
        var chip = paginaFria.Locator(".chip-filtro");
        await Expect(chip).ToHaveCountAsync(1);
        await Expect(chip).ToContainTextAsync(nombreEmpresa!);

        await paginaFria.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Limpiar todo", Exact = true }).ClickAsync();

        // Barrera positiva antes de afirmar la ausencia: el chip desaparece cuando
        // el circuito ya procesó el clic; solo entonces la URL sin filtro prueba algo.
        await Expect(paginaFria.Locator(".chip-filtro")).ToHaveCountAsync(0);
        await Expect(paginaFria).ToHaveURLAsync(new Regex(@"/trabajadores/?$"));
    }
}
