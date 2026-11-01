using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// La caducidad por inactividad (<c>CaducidadSesion</c>, ASVS V3.3.2) en un navegador real y con el
/// circuito de Blazor abierto, que es lo que <c>InactividadSesionE2ETests</c> no cubre (solo cookie y
/// señal) y los tests de Web.Tests no pueden cubrir (reloj manual, sin navegador ni SignalR).
///
/// <para>
/// <b>Qué observa.</b> El circuito abierto vuelve solo a la pantalla de acceso cuando ha pasado el plazo
/// del Tenant propietario de la cuenta sin una interacción de la persona, sin que la página haga ninguna
/// petición; y la misma cookie, pedida a mano, deja de valer. Cada caducidad lleva su control positivo en
/// el mismo recorrido: poco antes del plazo la sesión sigue viva, para que «cayó» no se lea por un fallo
/// que la hubiera tumbado de todos modos.
/// </para>
///
/// <para>
/// <b>Reglas del instrumento.</b> (1) Tras la primera interacción, la página no hace ningún clic más:
/// <c>actividad-sesion.js</c> aplaza las señales a una por minuto reloj, y una señal aplazada saldría en
/// mitad de otra fase y renovaría la sesión. (2) «Circuito vivo» se afirma por ausencia de navegación al
/// acceso tras varios ciclos de revalidación: el circuito caído siempre se manifiesta así
/// (<c>SesionDelCircuito</c>). Esa espera es un reloj del servidor, sin señal observable por ciclo.
/// </para>
/// </summary>
[Collection("AppCollectionRevalidacionRapida")]
public class ExpiracionPorInactividadE2ETests(WebAppFixtureConRevalidacionRapida fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// La fixture se comparte con <see cref="DesactivarCortaLaSesionTests"/>: se le devuelve el reloj real y el plazo
    /// por defecto de todos los Tenants (30 minutos) para que no herede ni un reloj adelantado ni un plazo de 5.
    /// Corre cuando ya se cerraron los contextos del navegador, así que ninguna sesión viva ve el reloj retroceder.
    /// </summary>
    public async Task DisposeAsync()
    {
        fixture.RestablecerReloj();
        await fixture.EjecutarSqlAsync("""UPDATE "ParametrosSistema" SET "MinutosInactividadSesion" = 30""");
    }

    private const string RutaAcceso = "/cuenta/iniciar-sesion";
    private static readonly Regex Acceso = new(Regex.Escape(RutaAcceso));

    // Duplican los nombres de la siembra (este proyecto no referencia Infrastructure), como GestorCaeCarteraMultiTenantTests.
    private const string EmailGestorCaeConCartera = "gestor.cartera.e2e@caemanager.local";
    private const string TenantBeneficiarioA = "Conservas Albatros S.L. (Tenant beneficiario E2E)";

    /// <summary>
    /// Tres ciclos de la revalidación del circuito (2 s cada uno en esta fixture): el tiempo en que un circuito
    /// que debiera caer ya habría vuelto al acceso. Reloj del servidor, sin señal observable por ciclo.
    /// </summary>
    private static Task EsperarCiclosDeRevalidacionAsync() => Task.Delay(TimeSpan.FromSeconds(6));

    [Fact]
    public async Task Con_el_circuito_abierto_la_sesion_cae_al_pasar_el_plazo_del_Tenant_sin_interaccion()
    {
        var email = Ayudas.EmailPrueba("gestorcae", 1);
        await FijarPlazoDeLaCuentaAsync(email, 5);

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, email, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes");

        // Control positivo: antes del plazo (4 de 5 minutos) la cookie vale y el circuito sigue abierto.
        fixture.AdelantarReloj(TimeSpan.FromMinutes(4));
        Assert.Equal(HttpStatusCode.OK, (await PedirAsync(contexto, "/clientes")).Estado);
        await EsperarCiclosDeRevalidacionAsync();
        Assert.DoesNotContain(RutaAcceso, page.Url);

        // Pasado el plazo (6 minutos), sin que la persona haya tocado nada: el circuito, que no hace
        // peticiones, vuelve al acceso solo, y la cookie ya no vale.
        fixture.AdelantarReloj(TimeSpan.FromMinutes(2));
        await Assertions.Expect(page).ToHaveURLAsync(Acceso, new PageAssertionsToHaveURLOptions { Timeout = 30_000 });
        AfirmarCaducada(await PedirAsync(contexto, "/clientes"));
    }

    [Fact]
    public async Task Una_interaccion_reinicia_el_plazo_y_la_sesion_cae_contando_desde_la_ultima()
    {
        var email = Ayudas.EmailPrueba("gestorcae", 1);
        await FijarPlazoDeLaCuentaAsync(email, 5);

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, email, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes");

        // Una interacción real a los 4 minutos (la primera señal del documento sale sin aplazarse).
        fixture.AdelantarReloj(TimeSpan.FromMinutes(4));
        var senal = page.WaitForResponseAsync(r =>
            r.Request.Method == "POST" && r.Url.EndsWith("/sesion/actividad", StringComparison.Ordinal));
        await page.Locator("body").ClickAsync(new LocatorClickOptions { Position = new Position { X = 1, Y = 1 } });
        Assert.Equal(204, (await senal).Status);

        // 8 minutos desde el inicio de sesión, 4 desde la interacción: sigue viva, la cookie y el circuito.
        // Sin el reinicio de la señal habría caído a los 5.
        fixture.AdelantarReloj(TimeSpan.FromMinutes(4));
        Assert.Equal(HttpStatusCode.OK, (await PedirAsync(contexto, "/clientes")).Estado);
        await EsperarCiclosDeRevalidacionAsync();
        Assert.DoesNotContain(RutaAcceso, page.Url);

        // 6 minutos desde la interacción, sin otra: cae.
        fixture.AdelantarReloj(TimeSpan.FromMinutes(2));
        await Assertions.Expect(page).ToHaveURLAsync(Acceso, new PageAssertionsToHaveURLOptions { Timeout = 30_000 });
        AfirmarCaducada(await PedirAsync(contexto, "/clientes"));
    }

    /// <summary>
    /// Plazo del Tenant propietario de la cuenta, no del visitado (<c>TenantClaimsPrincipalFactory</c>).
    /// El Gestor CAE del Operador CAE externo visita un Tenant beneficiario con un plazo más largo que el
    /// suyo: cae con el de su Tenant. Con la factoría leyendo el Tenant visitado no caería.
    /// </summary>
    [Fact]
    public async Task Visitando_un_Tenant_con_plazo_mas_largo_rige_el_plazo_del_Tenant_propietario_de_la_cuenta()
    {
        await FijarPlazoDeLaCuentaAsync(EmailGestorCaeConCartera, 5);
        await FijarPlazoDelTenantAsync(TenantBeneficiarioA, 480);

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await EntrarVisitandoAsync(page, TenantBeneficiarioA);

        fixture.AdelantarReloj(TimeSpan.FromMinutes(4));
        Assert.Equal(HttpStatusCode.OK, (await PedirAsync(contexto, "/mi-trabajo")).Estado);
        await EsperarCiclosDeRevalidacionAsync();
        Assert.DoesNotContain(RutaAcceso, page.Url);

        fixture.AdelantarReloj(TimeSpan.FromMinutes(2));
        await Assertions.Expect(page).ToHaveURLAsync(Acceso, new PageAssertionsToHaveURLOptions { Timeout = 30_000 });
        AfirmarCaducada(await PedirAsync(contexto, "/mi-trabajo"));
    }

    /// <summary>
    /// La otra dirección: el Tenant visitado tiene un plazo mínimo (5) y el de la cuenta el máximo (480).
    /// No cae a los 6 minutos con el plazo del visitado ni con el por defecto (30): cae a los 481, con el suyo.
    /// </summary>
    [Fact]
    public async Task Visitando_un_Tenant_con_plazo_mas_corto_no_cae_por_el_del_visitado_sino_por_el_de_la_cuenta()
    {
        await FijarPlazoDeLaCuentaAsync(EmailGestorCaeConCartera, 480);
        await FijarPlazoDelTenantAsync(TenantBeneficiarioA, 5);

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await EntrarVisitandoAsync(page, TenantBeneficiarioA);

        // 6 minutos: ya pasó el plazo del Tenant visitado (5) pero no el de la cuenta (480).
        fixture.AdelantarReloj(TimeSpan.FromMinutes(6));
        Assert.Equal(HttpStatusCode.OK, (await PedirAsync(contexto, "/mi-trabajo")).Estado);
        await EsperarCiclosDeRevalidacionAsync();
        Assert.DoesNotContain(RutaAcceso, page.Url);

        // 481 minutos (por debajo del tope de 12 h): pasado el plazo de 480 de su Tenant.
        fixture.AdelantarReloj(TimeSpan.FromMinutes(475));
        await Assertions.Expect(page).ToHaveURLAsync(Acceso, new PageAssertionsToHaveURLOptions { Timeout = 30_000 });
        AfirmarCaducada(await PedirAsync(contexto, "/mi-trabajo"));
    }

    private async Task EntrarVisitandoAsync(IPage page, string nombreTenantVisitado)
    {
        var tenantVisitado = await fixture.LeerValorSqlAsync(
            """SELECT "Id"::text FROM "Tenants" WHERE "Nombre" = @n""", ("n", nombreTenantVisitado));
        var tenantPropio = await fixture.LeerValorSqlAsync(
            """SELECT "TenantId"::text FROM "AspNetUsers" WHERE "Email" = @e""", ("e", EmailGestorCaeConCartera));
        Assert.NotEqual(tenantPropio, tenantVisitado);

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, EmailGestorCaeConCartera, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.CambiarClienteActivoAsync(page, fixture.BaseUrl, nombreTenantVisitado);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/mi-trabajo");

        // Control: la cuenta está de verdad en el Tenant visitado, no en el suyo.
        Assert.Equal(tenantVisitado, await Ayudas.TenantActivoIdAsync(page));
    }

    private async Task FijarPlazoDeLaCuentaAsync(string email, int minutos) =>
        Assert.Equal(1, await fixture.EjecutarSqlAsync(
            """
            UPDATE "ParametrosSistema" SET "MinutosInactividadSesion" = @m::int
            WHERE "TenantId" = (SELECT "TenantId" FROM "AspNetUsers" WHERE "Email" = @e)
            """,
            ("m", minutos.ToString()), ("e", email)));

    private async Task FijarPlazoDelTenantAsync(string nombreTenant, int minutos) =>
        Assert.Equal(1, await fixture.EjecutarSqlAsync(
            """
            UPDATE "ParametrosSistema" SET "MinutosInactividadSesion" = @m::int
            WHERE "TenantId" = (SELECT "Id" FROM "Tenants" WHERE "Nombre" = @n)
            """,
            ("m", minutos.ToString()), ("n", nombreTenant)));

    /// <summary>
    /// GET con la cookie del contexto y nada más (sin cookie jar, sin seguir redirecciones): 200 = sesión
    /// válida, 302 hacia el acceso = rechazada. No es una interacción: solo cuenta POST /sesion/actividad.
    /// </summary>
    private async Task<(HttpStatusCode Estado, string? Destino)> PedirAsync(IBrowserContext contexto, string ruta)
    {
        var cookie = string.Join("; ", (await contexto.CookiesAsync()).Select(c => $"{c.Name}={c.Value}"));
        using var manejador = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var cliente = new HttpClient(manejador) { BaseAddress = new Uri(fixture.BaseUrl) };
        using var peticion = new HttpRequestMessage(HttpMethod.Get, ruta);
        peticion.Headers.Add("Cookie", cookie);
        using var respuesta = await cliente.SendAsync(peticion);
        return (respuesta.StatusCode, respuesta.Headers.Location?.ToString());
    }

    private static void AfirmarCaducada((HttpStatusCode Estado, string? Destino) respuesta)
    {
        Assert.Equal(HttpStatusCode.Redirect, respuesta.Estado);
        Assert.Contains(RutaAcceso, respuesta.Destino);
    }
}
