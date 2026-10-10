using ClosedXML.Excel;
using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Las dos entradas de exportación de los listados (decisión D2 del 2026-10-08),
/// con la aplicación real: routing, enlace de los parámetros de consulta,
/// autorización y Tenant de la sesión. «Exportar todo» va sin criterios;
/// «Exportar esta vista» lleva al endpoint los de la pantalla, y el endpoint los
/// pasa a la misma consulta del listado.
///
/// <para>
/// Hasta el 2026-10-09 los seis endpoints llamaban a esa consulta con
/// <c>Busqueda: null</c>: cualquier parámetro se ignoraba y salía todo. La
/// búsqueda sin coincidencias es la que lo delata: tiene que dejar el libro con
/// la cabecera y ninguna fila, habiendo filas sin ella (control positivo).
/// </para>
///
/// <para>
/// <c>APIRequest</c> y <c>MaxRedirects = 0</c> por los mismos motivos que
/// <see cref="IncidenciasExportarAutorizacionE2ETests"/>.
/// </para>
/// </summary>
[Collection("AppCollectionListados")]
public class ExportarEstaVistaE2ETests(WebAppFixtureListados fixture)
{
    private const string BusquedaSinCoincidencias = "zzqx-sin-coincidencias-9917";

    [Theory]
    [InlineData("/trabajadores/exportar.xlsx", "Trabajadores")]
    [InlineData("/empresas/exportar.xlsx", "Empresas")]
    [InlineData("/clientes/exportar.xlsx", "Clientes")]
    [InlineData("/centros/exportar.xlsx", "Centros")]
    [InlineData("/subcontratas/exportar.xlsx", "Subcontratas")]
    [InlineData("/documentos/exportar.xlsx", "Documentos")]
    public async Task Exportar_esta_vista_respeta_la_busqueda_y_exportar_todo_no(string ruta, string hoja)
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);

        var todo = await FilasAsync(contexto, ruta, hoja);
        Assert.True(todo.Count > 0, $"{ruta} sin criterios no devolvió ninguna fila: sin datos, el filtro no se puede medir.");

        var sinCoincidencias = await FilasAsync(contexto, $"{ruta}?q={BusquedaSinCoincidencias}", hoja);
        Assert.Empty(sinCoincidencias);
    }

    /// <summary>
    /// El criterio selecciona, no solo vacía: buscar el valor de la primera fila
    /// la devuelve y deja fuera a las que no lo contienen. En estos cuatro
    /// listados la primera columna del libro es un campo que el buscador mira.
    /// </summary>
    [Theory]
    [InlineData("/trabajadores/exportar.xlsx", "Trabajadores")]
    [InlineData("/empresas/exportar.xlsx", "Empresas")]
    [InlineData("/clientes/exportar.xlsx", "Clientes")]
    [InlineData("/subcontratas/exportar.xlsx", "Subcontratas")]
    public async Task Exportar_esta_vista_devuelve_el_subconjunto_que_contiene_lo_buscado(string ruta, string hoja)
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);

        var todo = await FilasAsync(contexto, ruta, hoja);
        Assert.NotEmpty(todo);
        var buscado = todo[0][0];

        var vista = await FilasAsync(contexto, $"{ruta}?q={Uri.EscapeDataString(buscado)}", hoja);

        Assert.NotEmpty(vista);
        Assert.True(vista.Count <= todo.Count);
        Assert.All(vista, fila => Assert.Contains(buscado, fila[0], StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Los criterios no abren nada que la variante sin criterios no abriera: el
    /// rol Cliente no ve <c>/clientes</c> y tampoco exporta «esta vista».
    /// </summary>
    [Fact]
    public async Task Rol_Cliente_no_puede_exportar_la_vista_de_clientes()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("cliente", 1), Ayudas.ContrasenaUsuariosPrueba);

        var respuesta = await contexto.APIRequest.GetAsync(
            $"{fixture.BaseUrl}/clientes/exportar.xlsx?q=a",
            new APIRequestContextOptions { MaxRedirects = 0 });

        Assert.True(respuesta.Status is >= 300 and < 400, $"GET /clientes/exportar.xlsx?q=a devolvió {respuesta.Status} para el rol Cliente.");
        Assert.Contains("acceso-denegado", respuesta.Headers.GetValueOrDefault("location") ?? string.Empty);
    }

    /// <summary>
    /// Las cuatro exportaciones del cierre de listados: quien entra en la página descarga el libro
    /// con su hoja, con y sin criterios. No se afirma el número de filas: la siembra de los E2E no
    /// garantiza datos en estas cuatro pantallas (el subconjunto y el aislamiento los mide
    /// <c>ExportacionVehiculosProyectosVisitasGestionesTests</c> contra PostgreSQL).
    /// </summary>
    [Theory]
    [InlineData("/vehiculos/exportar.xlsx", "Vehículos")]
    [InlineData("/proyectos/exportar.xlsx", "Proyectos")]
    [InlineData("/visitas/exportar.xlsx", "Visitas")]
    [InlineData("/gestiones/exportar.xlsx", "Gestiones")]
    public async Task Un_Gestor_CAE_descarga_el_libro_del_listado_con_y_sin_criterios(string ruta, string hoja)
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);

        foreach (var consulta in new[] { string.Empty, $"?q={BusquedaSinCoincidencias}" })
        {
            var respuesta = await contexto.APIRequest.GetAsync(
                $"{fixture.BaseUrl}{ruta}{consulta}", new APIRequestContextOptions { MaxRedirects = 0 });
            Assert.Equal(200, respuesta.Status);

            using var stream = new MemoryStream(await respuesta.BodyAsync());
            using var libro = new XLWorkbook(stream);
            var filas = libro.Worksheet(hoja).RangeUsed()!.RowCount() - 1;
            if (consulta.Length > 0)
                Assert.Equal(0, filas);
        }
    }

    /// <summary>
    /// El rol Cliente no entra en estas cuatro páginas (su <c>[Authorize(Roles = …)]</c>) y
    /// tampoco descarga su listado, ni entero ni «esta vista». Sin los roles declarados en el
    /// endpoint, la política por defecto lo dejaría pasar.
    /// </summary>
    [Theory]
    [InlineData("/vehiculos/exportar.xlsx")]
    [InlineData("/proyectos/exportar.xlsx")]
    [InlineData("/visitas/exportar.xlsx")]
    [InlineData("/gestiones/exportar.xlsx")]
    public async Task Rol_Cliente_no_puede_exportar_los_listados_operativos(string ruta)
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("cliente", 1), Ayudas.ContrasenaUsuariosPrueba);

        foreach (var consulta in new[] { string.Empty, "?q=a" })
        {
            var respuesta = await contexto.APIRequest.GetAsync(
                $"{fixture.BaseUrl}{ruta}{consulta}", new APIRequestContextOptions { MaxRedirects = 0 });

            Assert.True(respuesta.Status is >= 300 and < 400, $"GET {ruta}{consulta} devolvió {respuesta.Status} para el rol Cliente.");
            Assert.Contains("acceso-denegado", respuesta.Headers.GetValueOrDefault("location") ?? string.Empty);
        }
    }

    private async Task<List<List<string>>> FilasAsync(IBrowserContext contexto, string rutaConQuery, string hoja)
    {
        var respuesta = await contexto.APIRequest.GetAsync(
            $"{fixture.BaseUrl}{rutaConQuery}", new APIRequestContextOptions { MaxRedirects = 0 });
        Assert.Equal(200, respuesta.Status);

        using var stream = new MemoryStream(await respuesta.BodyAsync());
        using var libro = new XLWorkbook(stream);
        return libro.Worksheet(hoja).RangeUsed()!.Rows().Skip(1)
            .Select(fila => fila.Cells().Select(c => c.GetString()).ToList())
            .ToList();
    }
}
