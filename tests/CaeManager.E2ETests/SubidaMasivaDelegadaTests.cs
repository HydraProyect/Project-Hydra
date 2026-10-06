using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// /documentos/subida-masiva dentro de un Workspace operativo derivado
/// (Operador CAE externo operando un Tenant propietario ajeno por delegación).
///
/// <para>
/// <b>Defecto medido.</b> Con el rol propio la pantalla abría; con el rol
/// efectivo ajustado por <c>RolEfectivoDelWorkspaceMiddleware</c> (Consulta
/// delegada, Gestor CAE delegado) devolvía HTTP 500
/// (<c>InvalidOperationException</c>: segunda operación sobre el mismo
/// DbContext). La página consultaba <c>ObtenerRolEfectivoAsync</c> directamente en
/// <c>OnInitializedAsync</c>, en paralelo con el layout, y con un workspace
/// delegado esa consulta llega a base de datos (sin selección devuelve el claim
/// sin tocarla, por eso el rol propio no fallaba).
/// </para>
///
/// <para>
/// Cubre las dos entradas que se midieron: carga fría (recarga completa sobre la
/// URL) y clic desde /documentos. El enlace «Subida múltiple» además no debe
/// ofrecerse a quien no puede escribir (Consulta): la pantalla solo le enseña
/// «Solo lectura».
/// </para>
/// </summary>
[Collection("AppCollection")]
public class SubidaMasivaDelegadaTests(WebAppFixture fixture)
{
    private const string Ruta = "/documentos/subida-masiva";

    [Fact]
    public async Task Consulta_delegada_abre_la_subida_multiple_con_carga_fria_sin_500()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await EntrarEnWorkspaceDelegadoAsync(page, Ayudas.EmailOperadorConsultaConsultora);

        var respuesta = await page.GotoAsync($"{fixture.BaseUrl}{Ruta}");

        Assert.Equal(200, respuesta!.Status);
        await AfirmarSubidaMultipleAbiertaAsync(page);
        await Expect(page.GetByText("Solo lectura")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Gestor_CAE_delegado_abre_la_subida_multiple_con_carga_fria_sin_500()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await EntrarEnWorkspaceDelegadoAsync(page, Ayudas.EmailAdministradorConsultora);

        var respuesta = await page.GotoAsync($"{fixture.BaseUrl}{Ruta}");

        Assert.Equal(200, respuesta!.Status);
        await AfirmarSubidaMultipleAbiertaAsync(page);
        await Expect(page.GetByText("Arrastra aquí los documentos de trabajador")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Gestor_CAE_delegado_llega_a_la_subida_multiple_con_el_enlace_de_Documentos()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await EntrarEnWorkspaceDelegadoAsync(page, Ayudas.EmailAdministradorConsultora);

        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/documentos");
        // «Subida múltiple» vive ahora en el menú «Más» de la cabecera (una sola primaria, patrón de lista).
        await Ayudas.PulsarAccionDeMenuAsync(page.Locator(".cabecera-pagina .menu-acciones-disparador"), "Subida múltiple");

        await AfirmarSubidaMultipleAbiertaAsync(page);
    }

    [Fact]
    public async Task Consulta_delegada_no_ve_el_enlace_a_la_subida_multiple_en_Documentos()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await EntrarEnWorkspaceDelegadoAsync(page, Ayudas.EmailOperadorConsultaConsultora);

        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/documentos");

        // El menú también contiene exportaciones de lectura: abrirlo demuestra que
        // la cabecera funciona antes de comprobar que no ofrece acciones de escritura.
        var menu = await Ayudas.AbrirMenuAccionesAsync(page.Locator(".cabecera-pagina .menu-acciones-disparador"));
        await Expect(menu.GetByRole(AriaRole.Menuitem, new LocatorGetByRoleOptions { Name = "Exportar a Excel", Exact = true }))
            .ToBeVisibleAsync();
        await Expect(menu.GetByRole(AriaRole.Menuitem, new LocatorGetByRoleOptions { Name = "Subida múltiple", Exact = true }))
            .ToHaveCountAsync(0);
        await Expect(menu.GetByRole(AriaRole.Menuitem, new LocatorGetByRoleOptions { Name = "Importar documentos", Exact = true }))
            .ToHaveCountAsync(0);
    }

    private async Task EntrarEnWorkspaceDelegadoAsync(IPage page, string email)
    {
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, email, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.CambiarClienteActivoAsync(page, fixture, Ayudas.NombreClienteDelegadoDemo);
    }

    private static async Task AfirmarSubidaMultipleAbiertaAsync(IPage page)
    {
        Assert.DoesNotContain("/acceso-denegado", page.Url);
        await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Subida múltiple de documentos" })
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
    }

    private static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);
}
