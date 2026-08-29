using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// <b>¿Sobrevive la selección de workspace a una navegación DENTRO del circuito de
/// Blazor?</b>
///
/// <para>
/// La selección (workspace activo y, en el plano 3, sesión privilegiada) vive en una
/// cookie que <c>ClienteActivoSeleccionado</c> lee por <c>IHttpContextAccessor</c>.
/// Dentro de un circuito de Blazor Server ese <c>HttpContext</c> puede no existir, y
/// entonces la selección resuelve a nulo <b>y memoiza ese nulo</b> para todo el ámbito
/// de DI — medido en <c>SeleccionSinHttpContextTests</c> (Web.Tests).
/// </para>
///
/// <para>
/// <b>Por qué ningún test previo lo cubría.</b> Los cuatro E2E que cambian de
/// workspace navegan con <c>page.GotoAsync</c> o con el <c>&lt;form&gt;</c> del
/// selector, es decir con una petición HTTP completa donde <c>HttpContext</c> sí
/// existe. Su verde no dice nada sobre el circuito. Este test es el primero que
/// navega <b>sin recargar el documento</b>.
/// </para>
///
/// <para>
/// <b>Lo que está en juego.</b> Si la selección se pierde en el circuito, la
/// consecuencia inmediata es que <c>ITenantActual</c> resuelve al tenant de origen
/// dentro del workspace ajeno. Y para el plano 3 es peor:
/// <c>TenantRlsConnectionInterceptor</c> adopta el rol de solo lectura
/// <c>cae_app_soporte</c> <b>solo</b> cuando la sesión privilegiada no es nula, así
/// que la garantía que sostiene la decisión D-2 —el soporte no conserva escritura— no
/// aplicaría a nada de lo que ocurre por el circuito. El fallo sería silencioso: nada
/// se rompe, simplemente la protección no está.
/// </para>
/// </summary>
[Collection("AppCollection")]
public class SeleccionSobreviveAlCircuitoTests(WebAppFixture fixture)
{
    /// <summary>
    /// Marca puesta en <c>window</c> antes de navegar. Si sigue ahí después, el
    /// documento <b>no</b> se recargó y la navegación fue del router de Blazor. Sin
    /// esta comprobación el test podría pasar por el motivo contrario al que
    /// investiga: una recarga completa trae <c>HttpContext</c> y la selección
    /// funcionaría igual, sin haber ejercitado el circuito.
    /// </summary>
    private const string MarcaDeCircuito = "__marcaSinRecargaDeDocumento";

    [Fact]
    public async Task La_seleccion_de_workspace_sigue_viva_tras_navegar_dentro_del_circuito()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(
            page, fixture.BaseUrl, Ayudas.EmailAdministradorConsultora, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.DescartarNotificacionesPendientesAsync(page);

        // ── Línea base, en el mismo fixture ────────────────────────────────────
        // El tenant de origen del Administrador es la Consultora, que no tiene datos
        // operativos propios (ADR-004 § 5.1; los ~200 Clientes de la siembra viven en
        // su Delegated Workspace). Comprobarlo aquí es lo que da valor a la aserción
        // final: sin esta línea base, "hay empresas" al final podría significar
        // simplemente que el origen también las tenía.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/empresas");
        await Assertions.Expect(page.GetByText("Aún no hay empresas"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        // ── Cambio de workspace ───────────────────────────────────────────────
        // Vuelve al inicio primero: el selector redirige a returnUrl, y si el cambio
        // ocurriera estando ya en /empresas el paso siguiente no navegaría a ningún
        // sitio y no habría navegación de circuito que medir.
        await Ayudas.NavegarYEsperarAsync(page, fixture.BaseUrl);
        await Ayudas.CambiarClienteActivoAsync(page, fixture.BaseUrl, Ayudas.NombreClienteDelegadoDemo);

        // ── La navegación de circuito ─────────────────────────────────────────
        // Esperar a que el cambio de workspace haya ASENTADO antes de tocar el
        // contexto de JS. El <form> del selector dispara una navegación completa, y
        // EvaluateAsync no reintenta: sin esta espera revienta con "Execution context
        // was destroyed" (visto en la primera ejecución de este test). Un Locator sí
        // se vuelve a resolver contra el DOM actual, así que esta aserción sirve de
        // barrera y de comprobación de que el cambio surtió efecto — dos cosas que
        // interesan por separado.
        await Assertions.Expect(page.Locator(".selector-cliente-activo option:checked"))
            .ToHaveTextAsync(Ayudas.NombreClienteDelegadoDemo,
                new LocatorAssertionsToHaveTextOptions { Timeout = 15_000 });

        await page.EvaluateAsync($"window.{MarcaDeCircuito} = true;");

        var enlaceEmpresas = page.Locator("a.nav-item[href='empresas']").First;
        await Assertions.Expect(enlaceEmpresas).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await enlaceEmpresas.ClickAsync();

        await Assertions.Expect(page).ToHaveURLAsync(
            new System.Text.RegularExpressions.Regex("/empresas$"),
            new PageAssertionsToHaveURLOptions { Timeout = 15_000 });

        // Guarda del instrumento, antes de la aserción que importa: si el documento
        // se recargó, este test no ha medido el circuito y su resultado —verde o
        // rojo— no responde a la pregunta.
        var marcaSobrevive = await page.EvaluateAsync<bool?>($"window.{MarcaDeCircuito} ?? null");
        Assert.True(
            marcaSobrevive is true,
            "el clic en el menú recargó el documento entero, así que esta ejecución NO ha ejercitado la " +
            "navegación dentro del circuito. El test no ha medido lo que dice medir: hay que encontrar otra " +
            "vía de navegación que no recargue antes de creerse su resultado.");

        // ── La pregunta ───────────────────────────────────────────────────────
        await Assertions.Expect(page.GetByText("Aún no hay empresas")).Not.ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
    }
}
