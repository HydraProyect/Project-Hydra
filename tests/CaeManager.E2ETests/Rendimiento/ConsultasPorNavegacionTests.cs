using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace CaeManager.E2ETests.Rendimiento;

/// <summary>
/// <b>Consultas por navegación mejorada</b> de Empresas, Subcontratas e
/// Incidencias: guardián de regresión de la consulta de lista.
///
/// <para>
/// <b>Línea base medida</b> (Gestor CAE de Refrielectric, con
/// <see cref="MedidorConsultasSql"/>, 3 repeticiones idénticas): una
/// navegación mejorada ejecuta la pantalla dos veces —una en la pasada de
/// prerender y otra al conectar el circuito, cada una con su
/// <c>DbContext</c>—. La consulta de la lista son dos comandos por pasada
/// (el <c>count(*)</c> y la página), es decir 2 en un GET crudo y 4 en la
/// navegación; esta prueba cuenta el <c>count(*)</c>, que es <b>1 por pasada:
/// 1 en el prerender y 2 en la navegación</b>, en las tres pantallas. Es el
/// coste de hoy y la prueba lo fija como techo.
/// </para>
///
/// <para>
/// <b>Qué protege y qué no.</b> Se pone en rojo si alguien hace que una
/// pasada ejecute la consulta de la lista más de una vez (recarga doble en
/// <c>OnInitializedAsync</c>, una consulta repetida al cambiar un parámetro,
/// un handler que la lanza dos veces). Cuenta comandos SQL sobre la tabla de
/// la pantalla, no milisegundos: un N+1 por fila que consulte OTRA tabla no
/// lo ve. Si un cambio legítimo añade una consulta, se actualiza el techo en
/// el mismo cambio y se dice por qué.
/// </para>
///
/// <para>
/// Un intento de eliminar la segunda pasada (recoger el resultado del
/// prerender en el circuito con <c>PersistentComponentState</c>) bajaba 4 a 2
/// pero ahorraba solo 18–30 ms de esqueleto de carga al usuario, y se
/// descartó (#752).
/// </para>
/// </summary>
[Collection("AppCollectionConsultasSql")]
public class ConsultasPorNavegacionTests(WebAppFixtureConConsultasSql fixture)
{
    // Gestor CAE nativo de Refrielectric: su Tenant propietario tiene datos en
    // las tres pantallas (el Administrador de la Consultora ve listas vacías
    // sin workspace delegado, y una lista vacía no distingue «se ejecutó la
    // consulta» de «no había nada»).
    private const string Email = Ayudas.EmailGestorRefrielectric;

    // Techos medidos: count(*) de la lista en un GET crudo (prerender) y en
    // una navegación mejorada (prerender + circuito).
    private const int TechoPrerender = 1;
    private const int TechoNavegacion = 2;

    public static TheoryData<string, string, string> Pantallas => new()
    {
        { "empresas", "SELECT count(*)::int FROM \"Empresas\" AS e", ".tarjeta-fila-acordeon" },
        { "subcontratas", "SELECT count(*)::int FROM \"Empresas\" AS e", ".tarjeta-fila-acordeon" },
        { "incidencias", "SELECT count(*)::int FROM \"Incidencias\" AS i", "table.tabla-datos tbody tr" },
    };

    [Theory]
    [MemberData(nameof(Pantallas))]
    public async Task La_lista_se_consulta_como_mucho_una_vez_por_pasada(
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
        await medidor.EsperarSilencioAsync(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
        var consultasDelPrerender = MedidorConsultasSql.Contar(medidor.ComandosDesde(marcaPrerender), fragmentoSql);

        // Navegación mejorada (NavLink): prerender + circuito.
        var marcaNavegacion = medidor.MarcarAhora();
        var enlace = page.Locator($"a.nav-item[href='{ruta}']").First;
        await Assertions.Expect(enlace).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await enlace.ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(
            new Regex($"/{ruta}$"), new PageAssertionsToHaveURLOptions { Timeout = 15_000 });
        await medidor.EsperarSilencioAsync(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
        var consultasDeLaNavegacion = MedidorConsultasSql.Contar(medidor.ComandosDesde(marcaNavegacion), fragmentoSql);

        // Control positivo: el instrumento ve la consulta, y la pantalla tiene
        // filas (una lista vacía también ejecuta la consulta, pero no
        // demuestra que la pantalla llegó a pintar datos).
        Assert.True(
            consultasDelPrerender >= 1,
            $"Control positivo: el prerender de /{ruta} no ejecutó ninguna consulta «{fragmentoSql}»: el instrumento no ve la lista.");
        await Assertions.Expect(page.Locator(selectorFila).First).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        Assert.True(
            consultasDelPrerender <= TechoPrerender,
            $"/{ruta}: el prerender solo ejecutó {consultasDelPrerender} consultas «{fragmentoSql}» y el techo medido es {TechoPrerender}: " +
            "una pasada ejecuta la consulta de la lista más veces que la línea base.");
        Assert.True(
            consultasDeLaNavegacion <= TechoNavegacion,
            $"/{ruta}: la navegación mejorada ejecutó {consultasDeLaNavegacion} consultas «{fragmentoSql}» y el techo medido es {TechoNavegacion} " +
            "(prerender + circuito): la lista se consulta más veces que la línea base.");
    }
}
