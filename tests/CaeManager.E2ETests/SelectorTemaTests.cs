using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// <b>¿El tema elegido llega a verse?</b>
///
/// <para>
/// <c>SelectorTema</c> guarda la preferencia en la cuenta
/// (<c>ApplicationUser.Tema</c>) y la aplica sobre <c>&lt;html data-theme&gt;</c>
/// por interoperación de JS (<c>wwwroot/js/tema.js</c>). Son dos efectos
/// distintos y hasta ahora ninguna prueba cubría el segundo: el componente
/// guardaba la preferencia y no la aplicaba <b>nunca</b> —ni al cambiarla ni
/// al cargar la página—, porque la guarda de <c>OnAfterRenderAsync</c> no
/// podía dispararse (ver su doc-comment). El fallo era mudo: la cuenta
/// quedaba con el tema correcto y el usuario no veía ningún cambio.
/// </para>
///
/// <para>
/// Por eso este test comprueba las dos mitades por separado — se aplica <b>en
/// vivo</b> y sigue aplicado <b>tras recargar</b>. Solo la primera fallaría si
/// se rompiera la interoperación; solo la segunda, si se rompiera el guardado.
/// </para>
/// </summary>
[Collection("AppCollection")]
public class SelectorTemaTests(WebAppFixture fixture)
{
    [Fact]
    public async Task El_tema_elegido_se_aplica_al_documento_y_sobrevive_a_la_recarga()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(
            page, fixture.BaseUrl, Ayudas.EmailAdministradorConsultora, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, fixture.BaseUrl);

        // Línea base: sin preferencia explícita, "sistema" no pone el atributo
        // (ver tema.js) — así "aparece data-theme" significa algo.
        await Assertions.Expect(page.Locator("html")).Not.ToHaveAttributeAsync(
            "data-theme", "oscuro", new LocatorAssertionsToHaveAttributeOptions { Timeout = 10_000 });

        var selectorTema = page.GetByRole(AriaRole.Switch, new() { Name = "Tema oscuro" });
        await Assertions.Expect(selectorTema).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await Ayudas.ElegirTemaOscuroAsync(selectorTema, true);

        // (1) En vivo, por interoperación de JS sobre el documento actual.
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync(
            "data-theme", "oscuro", new LocatorAssertionsToHaveAttributeOptions { Timeout = 15_000 });

        // (2) Tras una carga completa: la preferencia se guardó en la cuenta y
        // el componente vuelve a aplicarla en el circuito nuevo.
        //
        // Navegar aquí es seguro SIN ninguna espera extra, y conviene saber
        // por qué antes de "arreglar" este punto con un sleep o un
        // NetworkIdle de más: la aserción (1) es una barrera real del
        // guardado, no una señal del cliente. CambiarTemaAsync persiste
        // ApplicationUser.Tema ANTES de pedirle a tema.js que aplique el
        // tema, así que ver data-theme en el DOM implica que el UPDATE ya
        // cuajó. Cuando el orden era el inverso (hasta 2026-09-20) esta
        // navegación adelantaba al guardado: el HTML salía con el tema
        // correcto desde la cookie y el circuito de /empresas lo borraba al
        // leer la fila sin actualizar — <html lang="en"> durante los 15 s
        // completos, que es como este test expulsó a la PR #756 de la cola
        // de fusión (run 35512763740). Reproducido inyectando un retardo en
        // GuardarTemaAsync: misma línea, mismo «unexpected value null».
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/empresas");
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync(
            "data-theme", "oscuro", new LocatorAssertionsToHaveAttributeOptions { Timeout = 15_000 });

        // Devuelve la cuenta a su estado inicial: la fixture es compartida por
        // toda "AppCollection" y este usuario lo usan otros tests.
        await Ayudas.ElegirTemaOscuroAsync(selectorTema, false);
        await Assertions.Expect(page.Locator("html")).Not.ToHaveAttributeAsync(
            "data-theme", "oscuro", new LocatorAssertionsToHaveAttributeOptions { Timeout = 15_000 });
    }

    /// <summary>
    /// tema.js dejó de escuchar 'enhancedload' (ver su comentario): con
    /// data-theme ya prerenderizado en el HTML que la navegación "enhanced"
    /// trae del servidor (ver <c>TemaCookie</c>, Web), el propio morph del
    /// DOM de Blazor lo conserva en &lt;html&gt; sin que ningún JS de esta app
    /// tenga que reaplicarlo. Este test es la comprobación de que quitar ese
    /// listener no lo rompió — antes de quitarlo, el mismo escenario (cambiar
    /// el tema en vivo y navegar sin recargar el documento) dependía de él.
    /// </summary>
    [Fact]
    public async Task El_tema_sobrevive_a_una_navegacion_mejorada_sin_recargar_el_documento()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(
            page, fixture.BaseUrl, Ayudas.EmailAdministradorConsultora, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, fixture.BaseUrl);

        var selectorTema = page.GetByRole(AriaRole.Switch, new() { Name = "Tema oscuro" });
        await Assertions.Expect(selectorTema).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await Ayudas.ElegirTemaOscuroAsync(selectorTema, true);
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync(
            "data-theme", "oscuro", new LocatorAssertionsToHaveAttributeOptions { Timeout = 15_000 });

        // Clic de menú lateral: navegación "enhanced" (petición HTTP real,
        // sin recargar el documento — ver el comentario de DEC-70/REC-162 en
        // SeleccionSobreviveAlCircuitoTests), no page.GotoAsync.
        var enlaceEmpresas = page.Locator("a.nav-item[href='empresas']").First;
        await Assertions.Expect(enlaceEmpresas).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await enlaceEmpresas.ClickAsync();

        await Assertions.Expect(page).ToHaveURLAsync(
            new System.Text.RegularExpressions.Regex("/empresas$"),
            new PageAssertionsToHaveURLOptions { Timeout = 15_000 });
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync(
            "data-theme", "oscuro", new LocatorAssertionsToHaveAttributeOptions { Timeout = 15_000 });

        // Devuelve la cuenta a su estado inicial: la fixture es compartida.
        // Recarga completa antes de tocar el selector — no reutiliza el que
        // ya tiene la página: el clic de arriba (navegación "enhanced")
        // puede haber recreado el componente SelectorTema (mismo remontado
        // que documenta SeleccionSobreviveAlCircuitoTests), con su interop
        // de JS (_modulo) todavía importándose en vuelo. Elegir "sistema"
        // contra ese componente a medio inicializar cae en la guarda `if
        // (_modulo is not null)` de CambiarTemaAsync: GuardarTemaAsync sí
        // persiste el cambio en la cuenta, pero el DOM no se actualiza — un
        // fallo real medido en CI (2026-09-18, PR #710) que no tiene nada
        // que ver con lo que este test mide, solo con la limpieza.
        await Ayudas.NavegarYEsperarAsync(page, fixture.BaseUrl);
        var selectorTemaTrasRecarga = page.GetByRole(AriaRole.Switch, new() { Name = "Tema oscuro" });
        await Assertions.Expect(selectorTemaTrasRecarga).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await Ayudas.ElegirTemaOscuroAsync(selectorTemaTrasRecarga, false);
        await Assertions.Expect(page.Locator("html")).Not.ToHaveAttributeAsync(
            "data-theme", "oscuro", new LocatorAssertionsToHaveAttributeOptions { Timeout = 15_000 });
    }

    /// <summary>
    /// Defecto de staging del 2026-10-02: en tema oscuro el avatar de un Tenant
    /// beneficiario sin logo (barra lateral y cabecera) salía como un cuadrado
    /// liso, porque fondo y letra resolvían al mismo color (--color-primary-100
    /// de fondo, --color-primary-700 remapeado a --color-primary-100 de letra).
    /// El contrato por token lo fija ContrasteDeAvataresPorTemaTests; esto mide
    /// el color que el navegador PINTA de verdad, con el tema aplicado por
    /// tema.js, y exige las iniciales a 4,5:1 (WCAG AA) en oscuro y en claro.
    /// </summary>
    [Fact]
    public async Task Las_iniciales_del_avatar_del_selector_de_empresa_se_leen_en_oscuro_y_en_claro()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(
            page, fixture.BaseUrl, Ayudas.EmailAdministradorConsultora, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, fixture.BaseUrl);

        // Este usuario alcanza varios Tenants beneficiarios: el selector de la barra lateral se pinta.
        var avatar = Ayudas.DisparadorSelectorTenant(page).Locator(".avatar-tenant");
        await Assertions.Expect(avatar).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        // El control mide iniciales, no un logo: si el Tenant de demo tuviera logo no habría letras que leer.
        await Assertions.Expect(avatar).Not.ToBeEmptyAsync(new LocatorAssertionsToBeEmptyOptions { Timeout = 5_000 });

        var selectorTema = page.GetByRole(AriaRole.Switch, new() { Name = "Tema oscuro" });
        await Assertions.Expect(selectorTema).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        const string medirContraste = """
            el => {
              const cs = getComputedStyle(el);
              const canal = c => { c /= 255; return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
              const lum = css => { const m = css.match(/\d+(\.\d+)?/g).map(Number); return 0.2126 * canal(m[0]) + 0.7152 * canal(m[1]) + 0.0722 * canal(m[2]); };
              const a = lum(cs.backgroundColor), b = lum(cs.color);
              return { fondo: cs.backgroundColor, letra: cs.color, ratio: (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05) };
            }
            """;

        try
        {
            // Termina en oscuro: así la espera del finally (claro ya no es oscuro) es una barrera real.
            foreach (var tema in new[] { "claro", "oscuro" })
            {
                await Ayudas.ElegirTemaOscuroAsync(selectorTema, tema == "oscuro");
                // El estado claro del interruptor es la ausencia de data-theme (ver SelectorTema).
                if (tema == "oscuro")
                    await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync(
                        "data-theme", "oscuro", new LocatorAssertionsToHaveAttributeOptions { Timeout = 15_000 });
                else
                    await Assertions.Expect(page.Locator("html")).Not.ToHaveAttributeAsync(
                        "data-theme", "oscuro", new LocatorAssertionsToHaveAttributeOptions { Timeout = 15_000 });

                var medida = await avatar.EvaluateAsync<System.Text.Json.JsonElement>(medirContraste);
                Assert.NotEqual("rgba(0, 0, 0, 0)", medida.GetProperty("fondo").GetString());
                var ratio = medida.GetProperty("ratio").GetDouble();
                Assert.True(ratio >= 4.5,
                    $"Iniciales del avatar en tema {tema}: {ratio:0.00}:1 (fondo {medida.GetProperty("fondo")}, " +
                    $"letra {medida.GetProperty("letra")}); WCAG AA exige 4,5:1");
            }
        }
        finally
        {
            // Devuelve la cuenta a su estado inicial: la fixture es compartida por toda "AppCollection".
            await Ayudas.ElegirTemaOscuroAsync(selectorTema, false);
            await Assertions.Expect(page.Locator("html")).Not.ToHaveAttributeAsync(
                "data-theme", "oscuro", new LocatorAssertionsToHaveAttributeOptions { Timeout = 15_000 });
        }
    }

    /// <summary>
    /// Seguimiento de #1037: tres restos de --color-primary-100 sin variante
    /// oscura. El contrato por token lo fija
    /// ContrasteDeSeleccionHoverYLogoPorTemaTests; esto mide lo que el navegador
    /// PINTA con el tema aplicado por tema.js: el texto seleccionado
    /// (::selection) y el elemento activo del menú lateral bajo el puntero a
    /// 4,5:1 (WCAG AA), y el token del fondo de los logos resuelto a blanco.
    /// Las transiciones se anulan para medir el hover ya asentado.
    /// </summary>
    [Fact]
    public async Task La_seleccion_y_el_hover_del_item_activo_se_leen_y_el_fondo_del_logo_es_blanco_en_oscuro_y_en_claro()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(
            page, fixture.BaseUrl, Ayudas.EmailAdministradorConsultora, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, fixture.BaseUrl);
        await page.AddStyleTagAsync(new PageAddStyleTagOptions
        {
            Content = "*, *::before, *::after { transition: none !important; }",
        });

        var selectorTema = page.GetByRole(AriaRole.Switch, new() { Name = "Tema oscuro" });
        await Assertions.Expect(selectorTema).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        const string selectorActivo = ".nav-principal a.nav-item.active";
        var activo = page.Locator(selectorActivo).First;
        await Assertions.Expect(activo).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        // Mide fondo y letra pintados de un elemento (o de su pseudoelemento ::selection).
        const string medirContraste = """
            args => {
              const el = args.selector ? document.querySelector(args.selector) : document.body;
              const cs = getComputedStyle(el, args.pseudo);
              const canal = c => { c /= 255; return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
              const lum = css => { const m = css.match(/\d+(\.\d+)?/g).map(Number); return 0.2126 * canal(m[0]) + 0.7152 * canal(m[1]) + 0.0722 * canal(m[2]); };
              const a = lum(cs.backgroundColor), b = lum(cs.color);
              return { fondo: cs.backgroundColor, letra: cs.color, ratio: (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05) };
            }
            """;

        try
        {
            // Termina en oscuro: así la espera del finally (claro ya no es oscuro) es una barrera real.
            foreach (var tema in new[] { "claro", "oscuro" })
            {
                await Ayudas.ElegirTemaOscuroAsync(selectorTema, tema == "oscuro");
                // El estado claro del interruptor es la ausencia de data-theme (ver SelectorTema).
                if (tema == "oscuro")
                    await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync(
                        "data-theme", "oscuro", new LocatorAssertionsToHaveAttributeOptions { Timeout = 15_000 });
                else
                    await Assertions.Expect(page.Locator("html")).Not.ToHaveAttributeAsync(
                        "data-theme", "oscuro", new LocatorAssertionsToHaveAttributeOptions { Timeout = 15_000 });

                var seleccion = await page.EvaluateAsync<System.Text.Json.JsonElement>(
                    medirContraste, new { selector = (string?)null, pseudo = "::selection" });
                Assert.True(seleccion.GetProperty("ratio").GetDouble() >= 4.5,
                    $"Texto seleccionado en tema {tema}: {seleccion.GetProperty("ratio").GetDouble():0.00}:1 " +
                    $"(fondo {seleccion.GetProperty("fondo")}, letra {seleccion.GetProperty("letra")}); WCAG AA exige 4,5:1");

                await activo.HoverAsync();
                // Sin esto, un overlay que se comiera el puntero mediría la base .active (que pasa AA) y no el hover.
                Assert.True(await activo.EvaluateAsync<bool>("el => el.matches(':hover')"),
                    "el puntero no quedó sobre el item activo: se estaría midiendo el estado base");
                var hover = await page.EvaluateAsync<System.Text.Json.JsonElement>(
                    medirContraste, new { selector = selectorActivo, pseudo = (string?)null });
                Assert.True(hover.GetProperty("ratio").GetDouble() >= 4.5,
                    $"Item activo bajo el puntero en tema {tema}: {hover.GetProperty("ratio").GetDouble():0.00}:1 " +
                    $"(fondo {hover.GetProperty("fondo")}, letra {hover.GetProperty("letra")}); WCAG AA exige 4,5:1");

                var fondoLogo = await page.EvaluateAsync<string>(
                    "() => getComputedStyle(document.documentElement).getPropertyValue('--color-logo-fondo').trim().toLowerCase()");
                Assert.Equal("#ffffff", fondoLogo);
            }
        }
        finally
        {
            // Devuelve la cuenta a su estado inicial: la fixture es compartida por toda "AppCollection".
            await Ayudas.ElegirTemaOscuroAsync(selectorTema, false);
            await Assertions.Expect(page.Locator("html")).Not.ToHaveAttributeAsync(
                "data-theme", "oscuro", new LocatorAssertionsToHaveAttributeOptions { Timeout = 15_000 });
        }
    }
}
