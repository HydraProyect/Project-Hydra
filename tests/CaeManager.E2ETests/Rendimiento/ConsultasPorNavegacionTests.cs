using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace CaeManager.E2ETests.Rendimiento;

/// <summary>
/// <b>Consultas por navegación mejorada</b> de las pantallas que usan
/// <c>EstadoDePantallaPersistido</c> (Empresas, Subcontratas, Incidencias).
///
/// <para>
/// Sin el patrón, una navegación mejorada ejecuta la pantalla dos veces: una
/// en la pasada de prerender y otra al conectar el circuito, cada una con su
/// <c>DbContext</c>. Línea base medida antes del cambio (Administrador de la
/// Consultora, <see cref="MedidorConsultasSql"/>): 4 comandos SQL sobre la
/// tabla propia de la pantalla por navegación (2 del prerender + 2 del
/// circuito) en Empresas, Subcontratas e Incidencias; GET crudo (solo
/// prerender): 2.
/// </para>
///
/// <para>
/// <b>Qué se afirma.</b> No un número absoluto sino una relación calibrada por
/// el propio test: la navegación mejorada no cuesta más que el prerender solo
/// (un GET crudo de la misma URL, sin circuito) y ese prerender ejecuta al
/// menos la consulta de la lista (control positivo: el instrumento ve las
/// consultas). Con el patrón quitado, la navegación cuesta el doble del
/// prerender y el test sale rojo.
/// </para>
/// </summary>
[Collection("AppCollectionConsultasSql")]
public class ConsultasPorNavegacionTests(WebAppFixtureConConsultasSql fixture)
{
    // Gestor CAE nativo de Refrielectric: su Tenant propietario tiene datos en
    // las tres pantallas (el Administrador de la Consultora ve listas vacías
    // sin workspace delegado, y una lista vacía no distingue «se recogió el
    // estado» de «no había nada»).
    private const string Email = Ayudas.EmailGestorRefrielectric;

    public static TheoryData<string, string, string> Pantallas => new()
    {
        { "empresas", "SELECT count(*)::int FROM \"Empresas\" AS e", ".tarjeta-fila-acordeon" },
        { "subcontratas", "SELECT count(*)::int FROM \"Empresas\" AS e", ".tarjeta-fila-acordeon" },
        { "incidencias", "SELECT count(*)::int FROM \"Incidencias\" AS i", "table.tabla-datos tbody tr" },
    };

    [Theory]
    [MemberData(nameof(Pantallas))]
    public async Task La_navegacion_mejorada_no_ejecuta_la_lista_mas_veces_que_el_prerender_solo(
        string ruta, string fragmentoSql, string selectorFila)
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Email, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.DescartarNotificacionesPendientesAsync(page);
        await Ayudas.NavegarYEsperarAsync(page, fixture.BaseUrl);

        var medidor = new MedidorConsultasSql(fixture);
        await medidor.EsperarSilencioAsync(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));

        // Solo prerender: GET crudo, sin circuito.
        var marcaPrerender = medidor.MarcarAhora();
        var respuesta = await contexto.APIRequest.GetAsync($"{fixture.BaseUrl}/{ruta}");
        Assert.Equal(200, respuesta.Status);
        var html = await respuesta.TextAsync();
        await medidor.EsperarSilencioAsync(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
        var consultasDelPrerender = MedidorConsultasSql.Contar(medidor.ComandosDesde(marcaPrerender), fragmentoSql);
        var filasDelPrerender = ContarFilas(html, selectorFila);

        // Navegación mejorada (NavLink): prerender + circuito.
        var marcaNavegacion = medidor.MarcarAhora();
        var enlace = page.Locator($"a.nav-item[href='{ruta}']").First;
        await Assertions.Expect(enlace).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await enlace.ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(
            new Regex($"/{ruta}$"), new PageAssertionsToHaveURLOptions { Timeout = 15_000 });
        await medidor.EsperarSilencioAsync(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
        var consultasDeLaNavegacion = MedidorConsultasSql.Contar(medidor.ComandosDesde(marcaNavegacion), fragmentoSql);

        Assert.True(
            consultasDelPrerender >= 1,
            $"Control positivo: el prerender de /{ruta} no ejecutó ninguna consulta «{fragmentoSql}»: el instrumento no ve la lista.");
        Assert.True(
            consultasDeLaNavegacion <= consultasDelPrerender,
            $"/{ruta}: la navegación mejorada ejecutó {consultasDeLaNavegacion} consultas «{fragmentoSql}» y el prerender solo {consultasDelPrerender}: " +
            "el circuito repitió la consulta de la lista en vez de recoger el estado persistido.");

        // Recoger el estado no puede dejar la pantalla distinta de la que el
        // prerender pintó: mismas filas, y ningún esqueleto (el circuito
        // recogió datos, no se quedó esperándolos).
        Assert.True(filasDelPrerender > 0, $"/{ruta}: sin filas en el prerender; el test no distinguiría «recogido» de «vacío».");
        await Assertions.Expect(page.Locator(selectorFila)).ToHaveCountAsync(
            filasDelPrerender, new LocatorAssertionsToHaveCountOptions { Timeout = 15_000 });
        await Assertions.Expect(page.Locator(".esqueleto-lista")).ToHaveCountAsync(0);
    }

    /// <summary>
    /// Lo que viaja al navegador. El estado persistido va en un comentario
    /// del HTML, protegido con Data Protection: el navegador lo transporta
    /// pero no lo lee. Se comprueba que (a) existe, (b) es un blob de Data
    /// Protection (prefijo «CfDJ8») y (c) ni la razón social de una fila ni
    /// los nombres de campo del JSON aparecen dentro de él, ni tal cual ni
    /// tras decodificar el base64 — lo cual, por sí solo, no prueba que haya
    /// cifrado (lo prueba el prefijo de Data Protection), pero sí que el
    /// estado NO es el JSON legible que un navegador o un intermediario
    /// podrían leer.
    /// </summary>
    [Fact]
    public async Task El_estado_persistido_viaja_cifrado_y_sin_datos_en_claro()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Email, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.DescartarNotificacionesPendientesAsync(page);

        var respuesta = await contexto.APIRequest.GetAsync($"{fixture.BaseUrl}/empresas");
        var html = await respuesta.TextAsync();

        var estado = Regex.Match(html, @"<!--Blazor-Server-Component-State:(\S+?)-->");
        Assert.True(estado.Success, "El prerender de /empresas no dejó estado persistido (el patrón no se ejecutó).");

        var carga = estado.Groups[1].Value;
        Assert.StartsWith("CfDJ8", carga);

        var razonSocial = Regex.Match(html, @"aria-label=""Ver los clientes empresariales de ([^""]+)""").Groups[1].Value;
        Assert.False(string.IsNullOrWhiteSpace(razonSocial), "No se encontró ninguna Empresa en el HTML del prerender.");

        // Dos lecturas del blob, ninguna con datos legibles: como texto tal
        // cual y como base64 decodificado.
        Assert.DoesNotContain(razonSocial, carga, StringComparison.OrdinalIgnoreCase);
        var descifrableComoTexto = Encoding.UTF8.GetString(Convert.FromBase64String(carga));
        Assert.DoesNotContain(razonSocial, descifrableComoTexto, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RazonSocial", descifrableComoTexto, StringComparison.Ordinal);
    }

    private static int ContarFilas(string html, string selector) => selector switch
    {
        ".tarjeta-fila-acordeon" => Regex.Matches(html, @"class=""tarjeta-fila-acordeon""").Count,
        // Las filas del cuerpo de la tabla de datos, no cualquier <tr> del HTML
        // (un menú o un modal con una tabla las desplazaría en silencio).
        "table.tabla-datos tbody tr" => Regex.Matches(
                Regex.Match(html, @"<table[^>]*class=""[^""]*\btabla-datos\b[^""]*""[^>]*>.*?</table>", RegexOptions.Singleline).Value,
                "<tbody[^>]*>.*?</tbody>", RegexOptions.Singleline)
            .Sum(cuerpo => Regex.Matches(cuerpo.Value, "<tr[ >]").Count),
        _ => throw new ArgumentOutOfRangeException(nameof(selector), selector, "Selector sin contador para el HTML crudo."),
    };
}
