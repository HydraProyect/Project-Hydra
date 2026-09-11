using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Hallazgo (2026-09-11): <c>/incidencias/exportar.xlsx</c>
/// (IncidenciasEndpoints.cs) no declaraba <c>.RequireAuthorization(...)</c>
/// propio, así que solo lo cubría el FallbackPolicy de Program.cs (usuario
/// autenticado, sin rol). La página <c>/incidencias</c> exige Administrador,
/// DireccionCae, CoordinadorCae, GestorCae o Consulta (Incidencias.razor) y
/// excluye explícitamente Cliente — pero el endpoint aceptaba cualquier rol
/// autenticado, Cliente incluido. Mismo patrón que ya estaba corregido en
/// ClientesEndpoints/AuditoriaEndpoints/FacturacionEndpoints/ReportesEndpoints;
/// Incidencias se había quedado sin el <c>RequireAuthorization</c> equivalente.
///
/// Se usa <c>IBrowserContext.APIRequest</c> en vez de <c>page.GotoAsync</c>
/// porque <c>Results.File(...)</c> con <c>Content-Disposition: attachment</c>
/// dispara una descarga real del navegador (no una navegación con URL
/// final observable) — <c>APIRequest</c> comparte el cookie jar del
/// contexto ya autenticado por <see cref="Ayudas.IniciarSesionAsync"/> y
/// devuelve la respuesta HTTP cruda, incluida la cabecera Location del
/// redirect si lo hay.
///
/// <c>MaxRedirects = 0</c> es imprescindible: como ya documenta
/// <see cref="Ayudas.CambiarClienteActivoAsync"/> (medido por mutación,
/// HO-136-05), bajo cookie authentication (ConfigureApplicationCookie en
/// Program.cs) un <c>Forbid()</c> de autorización no llega como 401/403 —
/// el middleware de Identity lo convierte en un 302 hacia
/// <c>AccessDeniedPath</c> ("/acceso-denegado"). Sin desactivar el
/// seguimiento automático de redirects, <c>APIRequest</c> seguiría ese 302
/// y devolvería el 200 de la propia página de acceso denegado, indistinguible
/// de "el servidor nunca redirigió". Por eso la prueba no se conforma con un
/// status fuera de 200: exige además que el destino del redirect sea
/// justamente esa ruta, no solo cualquier 3xx.
/// </summary>
[Collection("AppCollection")]
public class IncidenciasExportarAutorizacionE2ETests(WebAppFixture fixture)
{
    [Fact]
    public async Task Rol_Cliente_no_puede_descargar_el_excel_de_incidencias()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("cliente", 1), Ayudas.ContrasenaUsuariosPrueba);

        var respuesta = await contexto.APIRequest.GetAsync(
            $"{fixture.BaseUrl}/incidencias/exportar.xlsx",
            new APIRequestContextOptions { MaxRedirects = 0 });

        Assert.True(
            respuesta.Status is >= 300 and < 400,
            $"GET /incidencias/exportar.xlsx devolvió {respuesta.Status} para el rol Cliente (se esperaba una " +
            "redirección 3xx a /acceso-denegado). Cliente no ve /incidencias (Incidencias.razor exige " +
            "Administrador/DireccionCae/CoordinadorCae/GestorCae/Consulta), así que un 200 aquí es el endpoint " +
            "sirviendo el Excel completo de incidencias del tenant a un rol que ni siquiera ve la pantalla.");

        var destino = respuesta.Headers.GetValueOrDefault("location") ?? string.Empty;
        Assert.Contains(
            "acceso-denegado",
            destino);
    }

    /// <summary>
    /// Regla positiva junto a la negativa de arriba: sin esta, una corrección
    /// que se pasara de restrictiva (por ejemplo, exigir solo Administrador)
    /// dejaría el test de Cliente en verde sin que nadie note que GestorCae
    /// —que sí ve la página— perdió acceso al export.
    /// </summary>
    [Fact]
    public async Task Rol_GestorCae_si_puede_descargar_el_excel_de_incidencias()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);

        var respuesta = await contexto.APIRequest.GetAsync(
            $"{fixture.BaseUrl}/incidencias/exportar.xlsx",
            new APIRequestContextOptions { MaxRedirects = 0 });

        Assert.Equal(200, respuesta.Status);
    }
}
