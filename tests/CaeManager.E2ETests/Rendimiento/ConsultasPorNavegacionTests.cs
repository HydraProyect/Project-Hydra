using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace CaeManager.E2ETests.Rendimiento;

/// <summary>
/// <b>Consultas por navegación mejorada</b> de Empresas, Subcontratas e
/// Incidencias: guardián de regresión de la consulta de lista.
///
/// <para>
/// <b>Línea base medida</b> (Gestor CAE de Refrielectric, con
/// <see cref="MedidorConsultasSql"/>): con el prerender de <c>InteractiveServer</c>, una
/// navegación mejorada ejecutaba la pantalla dos veces —una en la pasada de
/// prerender y otra al conectar el circuito, cada una con su
/// <c>DbContext</c>—. Las tres pantallas ya no se prerenderizan
/// (<c>InteractiveServerRenderMode(prerender: false)</c>, como Mi trabajo): la lista se
/// consulta una sola vez, en el circuito. La prueba cuenta el <c>count(*)</c> de la lista
/// (en Subcontratas, el <c>SELECT</c> de la lista, que ya no lleva <c>count(*)</c>) y
/// fija <b>0 en el GET crudo y 1 en la navegación</b>.
/// </para>
///
/// <para>
/// <b>Qué protege y qué no.</b> Se pone en rojo si alguien devuelve el prerender a una de
/// estas pantallas (el GET crudo volvería a consultar la lista) o hace que la navegación
/// ejecute la consulta más de una vez (recarga doble en <c>OnInitializedAsync</c>, una
/// consulta repetida al cambiar un parámetro, un handler que la lanza dos veces). Cuenta
/// comandos SQL sobre la tabla de la pantalla, no milisegundos: un N+1 por fila que consulte
/// OTRA tabla no lo ve. Si un cambio legítimo añade una consulta, se actualiza el techo en
/// el mismo cambio y se dice por qué.
/// </para>
///
/// <para>
/// Antes de quitar el prerender se probó recoger su resultado en el circuito con
/// <c>PersistentComponentState</c>: bajaba 4 a 2 pero ahorraba solo 18–30 ms de esqueleto de
/// carga al usuario, y se descartó (#752). El layout sí lo usa desde el encargo 31 (migas,
/// selector de Tenant beneficiario y gate de términos): son lecturas pequeñas que se repiten en
/// cada visita, y solo se reutilizan con el mismo contexto y dentro de la caducidad de los
/// memos del layout.
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

    // Techos: count(*) de la lista en un GET crudo (sin prerender, ninguna) y en una navegación
    // mejorada (una, la del circuito).
    private const int TechoPrerender = 0;
    private const int TechoNavegacion = 1;
    public static TheoryData<string, string, string> Pantallas => new()
    {
        { "empresas", "SELECT count(*)::int FROM \"Empresas\" AS e", ".tarjeta-fila-acordeon" },
        // Subcontratas no ejecuta count(*) desde que tiene franja de estado: el estado documental no está
        // persistido, así que la consulta trae todas las filas que pasan los filtros, las cuenta por estado
        // y pagina en memoria (ObtenerSubcontratasQuery, camino con estado). Su comando de lista es ese
        // SELECT, uno por pasada, y es el que se cuenta.
        { "subcontratas", "SELECT e.\"Id\", e.\"RazonSocial\", e.\"Cif\", e.\"CreadoEnUtc\", e.\"NivelServicio\"", ".tarjeta-fila-acordeon" },
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
        await Ayudas.NavegarYEsperarAsync(page, fixture.BaseUrl);

        var medidor = new MedidorConsultasSql(fixture.DirectorioLogs, fixture.PatronFicheroLog);
        await medidor.EsperarSilencioAsync(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));

        // GET crudo, sin circuito: sin prerender no debe consultar la lista.
        var marcaPrerender = medidor.MarcarAhora();
        var respuesta = await contexto.APIRequest.GetAsync($"{fixture.BaseUrl}/{ruta}");
        Assert.Equal(200, respuesta.Status);
        await medidor.EsperarSilencioAsync(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
        var consultasDelGetCrudo = MedidorConsultasSql.Contar(medidor.ComandosDesde(marcaPrerender), fragmentoSql);

        // Navegación mejorada (NavLink): solo el circuito. Incidencias vuelve a tener su entrada del menú en Operación (PR8, P-6:
        // sin barra de pestañas en Documentos), así que se llega como las demás pantallas.
        var marcaNavegacion = medidor.MarcarAhora();
        var enlace = page.Locator($"a.nav-item[href='{ruta}']").First;
        await Assertions.Expect(enlace).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await enlace.ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(
            new Regex($"/{ruta}$"), new PageAssertionsToHaveURLOptions { Timeout = 15_000 });
        await medidor.EsperarSilencioAsync(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
        var consultasDeLaNavegacion = MedidorConsultasSql.Contar(medidor.ComandosDesde(marcaNavegacion), fragmentoSql);

        await Assertions.Expect(page.Locator(selectorFila).First).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        // Control positivo en el circuito: el instrumento ve la consulta de la navegación, y la pantalla
        // tiene filas (una lista vacía también ejecuta la consulta, pero no demuestra que llegó a pintar datos).
        Assert.True(
            consultasDeLaNavegacion >= 1,
            $"Control positivo: la navegación a /{ruta} no ejecutó ninguna consulta «{fragmentoSql}»: el instrumento no ve la lista.");
        Assert.True(
            consultasDelGetCrudo <= TechoPrerender,
            $"/{ruta} no se prerenderiza, pero el GET crudo ejecutó {consultasDelGetCrudo} consultas «{fragmentoSql}»: " +
            "ha vuelto el prerender y con él la carga doble en cada navegación.");
        Assert.True(
            consultasDeLaNavegacion <= TechoNavegacion,
            $"/{ruta}: la navegación mejorada ejecutó {consultasDeLaNavegacion} consultas «{fragmentoSql}» y el techo sin prerender " +
            $"es {TechoNavegacion}: la lista se consulta más de una vez.");
    }
}
