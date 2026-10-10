using System.Text.RegularExpressions;
using System.Web;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// La vista recordada de un listado (decisión del 2026-10-08), de punta a punta en un navegador: lo
/// que bUnit no ve es que la restauración ocurre DESPUÉS del prerender, sobre un circuito recién
/// conectado; que al salir por el menú el aviso de cambio de dirección llega con la página aún viva
/// (y por eso la vista se escribe sin esperar al rebote); y que lo recordado sobrevive a una carga
/// en frío del documento.
///
/// <para>
/// Dos recorridos, uno por familia de listado: Vehículos (rejilla, con el orden de columna en la
/// URL) y Centros (acordeón, con la agrupación). Cada uno pone una vista, sale, vuelve sin
/// parámetros y la encuentra; abre el listado con otra vista en la URL y manda la URL; pulsa
/// «Restablecer vista» y, al volver sin parámetros, sigue la vista de inicio. La vista de los dos
/// lleva además una búsqueda, y al volver NO está: la búsqueda libre es de la vista pero no se recuerda.
/// </para>
///
/// <para>
/// La vista recordada es estado persistente del Usuario. Estos recorridos usan un Gestor CAE de la
/// siembra con el que no entra ningún otro recorrido de <c>AppCollection</c>, empiezan olvidando
/// por la interfaz lo que hubiera (no dependen de cómo lo dejó una ejecución anterior) y, si se
/// quedan a medias, lo olvidan antes de fallar. El aislamiento entre recorridos de toda la suite
/// no depende de eso: lo da el inicio de sesión del arnés, y su control es el tercer test.
/// </para>
/// </summary>
[Collection("AppCollection")]
public class VistaRecordadaE2ETests(WebAppFixture fixture)
{
    private static readonly string Gestor = Ayudas.EmailPrueba("gestorcae", 3);
    private static readonly Regex AgrupadoPorCliente = new("^Agrupar: Cliente$");
    private static readonly Regex SinAgrupar = new("^Agrupar: no$");

    private const string ToastRestablecida = "Vista de fábrica restablecida.";

    [Fact]
    public async Task Vehiculos_recuerda_el_orden_y_no_la_busqueda_la_URL_manda_y_Restablecer_vista_la_olvida()
    {
        const string pastilla = "Empleador";
        const string placeholder = "Filtrar esta pantalla: nombre, modelo o matrícula";
        var listado = $"{fixture.BaseUrl}/vehiculos";
        var conUnaVista = $"{listado}?orden=matricula-desc";

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Gestor, Ayudas.ContrasenaUsuariosPrueba);

        try
        {
            await OlvidarLoRecordadoAsync(page, conUnaVista, pastilla);

            // --- Una vista propia: dos vehículos del test, buscados por su prefijo y ordenados por matrícula, de mayor a menor. ---
            var prefijo = $"VR-{Guid.NewGuid().ToString("N")[..8]}";
            var (alfa, beta) = ($"{prefijo} Alfa", $"{prefijo} Beta");
            await ListadosFase1VehiculosDocumentosTests.CrearVehiculoAsync(page, alfa, "Modelo de prueba", $"{prefijo}-A");
            await ListadosFase1VehiculosDocumentosTests.CrearVehiculoAsync(page, beta, "Modelo de prueba", $"{prefijo}-B");

            var buscador = page.GetByPlaceholder(placeholder);
            var nombres = page.Locator("tbody .celda-vehiculo .enlace-nombre-fila");
            await buscador.FillAsync(prefijo);
            await Expect(nombres).ToHaveCountAsync(2);

            // El botón de orden es el nativo de QuickGrid; la dirección vigente la dice la clase de su cabecera.
            var ordenarPorMatricula = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Matrícula", Exact = true });
            var cabeceraMatricula = page.Locator("thead th").Filter(new LocatorFilterOptions { Has = ordenarPorMatricula });
            await ordenarPorMatricula.ClickAsync();
            await Expect(cabeceraMatricula).ToHaveClassAsync(new Regex(@"\bcol-sort-asc\b"));
            await ordenarPorMatricula.ClickAsync();
            await Expect(cabeceraMatricula).ToHaveClassAsync(new Regex(@"\bcol-sort-desc\b"));
            await Expect(nombres).ToHaveTextAsync([beta, alfa]);
            await EsperarVistaEnLaUrlAsync(page, ("q", prefijo), ("orden", "matricula-desc"));

            // --- Salir por el menú escribe la vista en el acto; al volver sin parámetros, está el orden
            //     y no la búsqueda, que no se recuerda. ---
            await SalirAOtraPantallaAsync(page, placeholder);
            await Ayudas.NavegarYEsperarAsync(page, listado);
            await EsperarVistaEnLaUrlAsync(page, ("orden", "matricula-desc"));
            await Expect(cabeceraMatricula).ToHaveClassAsync(new Regex(@"\bcol-sort-desc\b"));
            await Expect(buscador).ToHaveValueAsync(string.Empty);

            // --- Con parámetros manda la URL: ni se añade el orden recordado ni se cambia la búsqueda. ---
            await Ayudas.NavegarYEsperarAsync(page, $"{listado}?q={Uri.EscapeDataString(alfa)}");
            await CircuitoAtendiendoAsync(page, pastilla);
            await Expect(nombres).ToHaveTextAsync([alfa]);
            Assert.Equal(new Dictionary<string, string> { ["q"] = alfa }, ParametrosDe(page.Url));

            // --- «Restablecer vista»: la URL queda sin parámetros de vista y el enlace desaparece. ---
            await RestablecerVistaAsync(page);
            await Expect(EnlaceRestablecer(page)).ToHaveCountAsync(0);
            await Expect(page).ToHaveURLAsync(new Regex("/vehiculos/?$"));
            await Expect(buscador).ToHaveValueAsync(string.Empty);

            // --- Salir y volver sin parámetros: ya no hay nada que restaurar. ---
            await SalirAOtraPantallaAsync(page, placeholder);
            await Ayudas.NavegarYEsperarAsync(page, listado);
            await CircuitoAtendiendoAsync(page, pastilla);
            Assert.Empty(ParametrosDe(page.Url));
            await Expect(EnlaceRestablecer(page)).ToHaveCountAsync(0);
            await Expect(buscador).ToHaveValueAsync(string.Empty);
        }
        catch
        {
            await OlvidarSinTaparElFalloAsync(contexto, conUnaVista, pastilla);
            throw;
        }
    }

    [Fact]
    public async Task Centros_recuerda_la_agrupacion_y_no_la_busqueda_la_URL_manda_y_Restablecer_vista_la_olvida()
    {
        const string pastilla = "Cliente";
        const string placeholder = "Filtrar esta pantalla: centro, código, Cliente o empresa";
        var listado = $"{fixture.BaseUrl}/centros";
        var conUnaVista = $"{listado}?agrupar=no";

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Gestor, Ayudas.ContrasenaUsuariosPrueba);

        try
        {
            await OlvidarLoRecordadoAsync(page, conUnaVista, pastilla);

            var buscador = page.GetByPlaceholder(placeholder);
            // El desplegable «Agrupar» dice en su pastilla la agrupación vigente («Agrupar: Cliente», «Agrupar: no»).
            var agrupar = Ayudas.DesplegableAgrupar(page);
            var conteo = page.Locator(".conteo-centros");
            await Expect(agrupar).ToHaveTextAsync(AgrupadoPorCliente);
            var conteoDeInicio = (await conteo.InnerTextAsync()).Trim();

            // --- Una vista propia: sin agrupar y buscando un Centro por su nombre. La siembra no da un
            //     nombre estable, así que se lee de la lista el de un Centro cualquiera. ---
            await Ayudas.ElegirAgrupacionAsync(page, "Sin agrupar", SinAgrupar);
            await Expect(agrupar).ToHaveTextAsync(SinAgrupar);
            var nombresDeCentro = page.Locator(".lista-filas-acordeon .fila-pulsable .enlace-nombre-fila");
            await Expect(nombresDeCentro).Not.ToHaveCountAsync(0);
            var nombre = (await nombresDeCentro.AllInnerTextsAsync()).Select(n => n.Trim()).Last(n => n.Length > 0);
            await buscador.FillAsync(nombre);
            // La barrera es la CIFRA, no el texto entero: al teclear, el rótulo pasa en el acto a «N centros
            // con estos filtros» con la cifra de antes, y solo cambia de cifra cuando la lista filtrada llega.
            var cifraDeInicio = Regex.Match(conteoDeInicio, @"^\d+");
            Assert.True(cifraDeInicio.Success, $"el recuento de Centros debe empezar por su cifra: «{conteoDeInicio}»");
            await Expect(conteo).Not.ToHaveTextAsync(new Regex($@"^{cifraDeInicio.Value}\b"));
            await EsperarVistaEnLaUrlAsync(page, ("q", nombre), ("agrupar", "no"));

            // --- Salir por el menú escribe la vista en el acto; al volver sin parámetros, está la
            //     agrupación y no la búsqueda, que no se recuerda: la lista vuelve a ser la entera. ---
            await SalirAOtraPantallaAsync(page, placeholder);
            await Ayudas.NavegarYEsperarAsync(page, listado);
            await EsperarVistaEnLaUrlAsync(page, ("agrupar", "no"));
            await Expect(buscador).ToHaveValueAsync(string.Empty);
            await Expect(agrupar).ToHaveTextAsync(SinAgrupar);
            await Expect(conteo).ToHaveTextAsync(new Regex($@"^{cifraDeInicio.Value}\b"));

            // --- Con parámetros manda la URL: agrupado y sin búsqueda, como dice ella. ---
            await Ayudas.NavegarYEsperarAsync(page, $"{listado}?orden=cumplimiento-desc");
            await CircuitoAtendiendoAsync(page, pastilla);
            await Expect(agrupar).ToHaveTextAsync(AgrupadoPorCliente);
            await Expect(conteo).ToHaveTextAsync(conteoDeInicio);
            await Expect(buscador).ToHaveValueAsync(string.Empty);
            Assert.Equal(new Dictionary<string, string> { ["orden"] = "cumplimiento-desc" }, ParametrosDe(page.Url));

            // --- «Restablecer vista»: la URL queda sin parámetros de vista y el enlace desaparece. ---
            await RestablecerVistaAsync(page);
            await Expect(EnlaceRestablecer(page)).ToHaveCountAsync(0);
            await Expect(page).ToHaveURLAsync(new Regex("/centros/?$"));

            // --- Salir y volver sin parámetros: ya no hay nada que restaurar. ---
            await SalirAOtraPantallaAsync(page, placeholder);
            await Ayudas.NavegarYEsperarAsync(page, listado);
            await CircuitoAtendiendoAsync(page, pastilla);
            Assert.Empty(ParametrosDe(page.Url));
            await Expect(EnlaceRestablecer(page)).ToHaveCountAsync(0);
            await Expect(agrupar).ToHaveTextAsync(AgrupadoPorCliente);
        }
        catch
        {
            await OlvidarSinTaparElFalloAsync(contexto, conUnaVista, pastilla);
            throw;
        }
    }

    /// <summary>
    /// Control del arnés, no del producto. Los recorridos comparten cuentas de la siembra, y lo que uno deja
    /// recordado se le restauraría al siguiente: <see cref="Ayudas.IniciarSesionAsync"/> lo olvida antes de
    /// entrar (<see cref="WebAppFixture.OlvidarVistasRecordadasAsync"/>). Aquí se comprueba que ese borrado
    /// borra de verdad: una vista que consta recordada —se restauró en una carga en frío— deja de
    /// restaurarse tras un inicio de sesión nuevo. Sin él, el orden de los tests decidiría quién falla.
    /// </summary>
    [Fact]
    public async Task Un_inicio_de_sesion_del_arnes_deja_al_Usuario_sin_vista_recordada()
    {
        const string pastilla = "Cliente";
        const string placeholder = "Filtrar esta pantalla: centro, código, Cliente o empresa";
        var listado = $"{fixture.BaseUrl}/centros";

        await using (var contexto = await fixture.Browser.NewContextAsync())
        {
            var page = await contexto.NewPageAsync();
            await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Gestor, Ayudas.ContrasenaUsuariosPrueba);
            await Ayudas.NavegarYEsperarAsync(page, listado);
            await CircuitoAtendiendoAsync(page, pastilla);
            // Recién cargada en frío, la página aún se repinta al terminar de inicializarse y un clic suelto en
            // «Sin agrupar» puede perderse (visto 1 de 5 veces): la ayuda de la suite insiste hasta que el
            // desplegable dice «Agrupar: no».
            await Ayudas.MostrarCentrosSinAgruparAsync(page);
            await EsperarVistaEnLaUrlAsync(page, ("agrupar", "no"));
            await SalirAOtraPantallaAsync(page, placeholder);

            // Control: la vista consta recordada, porque una carga en frío sin parámetros la restaura.
            await Ayudas.NavegarYEsperarAsync(page, listado);
            await EsperarVistaEnLaUrlAsync(page, ("agrupar", "no"));
        }

        await using var otroContexto = await fixture.Browser.NewContextAsync();
        var otraPagina = await otroContexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(otraPagina, fixture.BaseUrl, Gestor, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(otraPagina, listado);
        await CircuitoAtendiendoAsync(otraPagina, pastilla);
        Assert.Empty(ParametrosDe(otraPagina.Url));
        await Expect(EnlaceRestablecer(otraPagina)).ToHaveCountAsync(0);
    }

    private static ILocator EnlaceRestablecer(IPage page) =>
        page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Restablecer vista", Exact = true });

    private static Dictionary<string, string> ParametrosDe(string url)
    {
        var consulta = HttpUtility.ParseQueryString(new Uri(url).Query);
        return consulta.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => consulta[k]!);
    }

    /// <summary>La URL lleva exactamente esos parámetros. La página la escribe por el circuito, sin recargar el documento.</summary>
    private static Task EsperarVistaEnLaUrlAsync(IPage page, params (string Parametro, string Valor)[] vista) =>
        page.WaitForURLAsync(
            url =>
            {
                var parametros = ParametrosDe(url);
                return parametros.Count == vista.Length && vista.All(p => parametros.GetValueOrDefault(p.Parametro) == p.Valor);
            },
            new PageWaitForURLOptions { Timeout = 30_000 });

    /// <summary>
    /// Dos idas y vueltas por el circuito que no tocan la vista: abrir y cerrar una pastilla de filtro.
    /// Tras una carga en frío hace de barrera doble. El listado llega prerenderizado y un clic anterior
    /// al circuito se pierde: cuando la pastilla responde, el circuito atiende. Y la pieza lee lo
    /// recordado en el primer render con circuito, antes de que estos dos clics lleguen: si fuera a
    /// restaurar algo, ya lo habría hecho, así que afirmar después que NO restauró prueba algo.
    /// </summary>
    private static async Task CircuitoAtendiendoAsync(IPage page, string pastilla)
    {
        var disparador = page.Locator(".barra-filtros-pastillas")
            .GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = pastilla, Exact = true });
        await Ayudas.AbrirMenuAccionesAsync(disparador);
        await disparador.PressAsync("Escape");
        await Expect(disparador).ToHaveAttributeAsync("aria-expanded", "false");
    }

    /// <summary>
    /// Pulsa «Restablecer vista» y espera al aviso, que sale cuando el servidor ya olvidó lo recordado.
    /// Quien llama viene siempre de una carga en frío, así que no queda en pantalla el aviso de otra vez.
    /// </summary>
    private static async Task RestablecerVistaAsync(IPage page)
    {
        await EnlaceRestablecer(page).ClickAsync();
        await Expect(page.Locator(".anfitrion-toasts")).ToContainTextAsync(ToastRestablecida);
    }

    /// <summary>
    /// Deja al Usuario sin vista recordada en ese listado, la tuviera o no: con una vista en la URL
    /// manda la URL y la barra ofrece siempre «Restablecer vista», que olvida lo recordado.
    /// </summary>
    private static async Task OlvidarLoRecordadoAsync(IPage page, string listadoConUnaVista, string pastilla)
    {
        await Ayudas.NavegarYEsperarAsync(page, listadoConUnaVista);
        await CircuitoAtendiendoAsync(page, pastilla);
        await RestablecerVistaAsync(page);
        await Expect(EnlaceRestablecer(page)).ToHaveCountAsync(0);
    }

    /// <summary>El recorrido se quedó a medias: se olvida la vista en una pestaña nueva, y si eso también falla manda el fallo del recorrido.</summary>
    private static async Task OlvidarSinTaparElFalloAsync(IBrowserContext contexto, string listadoConUnaVista, string pastilla)
    {
        try
        {
            await OlvidarLoRecordadoAsync(await contexto.NewPageAsync(), listadoConUnaVista, pastilla);
        }
        catch (Exception)
        {
            // Ni PlaywrightException ni TimeoutException deben sustituir a la excepción que explica el rojo.
        }
    }

    /// <summary>
    /// Sale a Empresas por el menú lateral: navegación sin recargar el documento, la que deja a la página
    /// escribir lo pendiente. La URL cambia antes de que el DOM se parchee; la barrera es que el buscador
    /// del listado de partida ya no esté. El enlace se busca dentro de su grupo del menú: las filas
    /// fijadas son copias suyas y viven fuera.
    /// </summary>
    private static async Task SalirAOtraPantallaAsync(IPage page, string placeholderDelListado)
    {
        await page.Locator(".nav-principal .nav-grupo-detalle a.nav-item[href='empresas']").ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex("/empresas/?$"), new PageAssertionsToHaveURLOptions { Timeout = 15_000 });
        await Expect(page.GetByPlaceholder(placeholderDelListado)).ToHaveCountAsync(0);
    }
}
