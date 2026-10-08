using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

[Collection("AppCollection")]
public class ListadosPropuestaGeometriaTests(WebAppFixture fixture)
{
    [Theory]
    [InlineData("vehiculos", ".celda-vehiculo")]
    [InlineData("documentos", ".documentos-propietario")]
    [InlineData("gestiones", ".gestion-trabajador-centro")]
    public async Task Las_filas_reales_respetan_la_geometria_de_la_propuesta(string pantalla, string identidad)
    {
        await using var contexto = await fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        { ViewportSize = new ViewportSize { Width = 1440, Height = 1000 }, ReducedMotion = ReducedMotion.Reduce });
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl,
            Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.DescartarNotificacionesPendientesAsync(page);
        await ElegirEmpresaSembradaAsync(page);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/{pantalla}");
        var tabla = page.Locator($".listado-propuesta-{pantalla} table.tabla-listado-propuesta");
        await Expect(tabla).ToHaveCountAsync(1);
        await Expect(tabla).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Table)).ToHaveCountAsync(1);
        await Expect(tabla.GetByRole(AriaRole.Columnheader)).ToHaveCountAsync(pantalla switch
        { "documentos" => 7, "vehiculos" => 5, _ => 6 });
        var filas = tabla.Locator("tbody > tr").Filter(new LocatorFilterOptions { Has = page.Locator(identidad) });
        await Expect(filas).Not.ToHaveCountAsync(0);
        Assert.True(await filas.CountAsync() > 0, $"Control positivo ausente: {pantalla} no tiene filas de datos reales.");
        await page.EvaluateAsync("() => document.fonts.ready.then(() => true)");
        await ComprobarAsync(tabla, filas, pantalla);
        await CapturarAsync(page, pantalla, "lectura");
        foreach (var fila in await filas.AllAsync())
        {
            var menu = fila.Locator(".lp-col-menu .menu-acciones-disparador");
            await menu.FocusAsync();
            await menu.PressAsync("Enter");
            await Expect(menu).ToHaveAttributeAsync("aria-expanded", "true");
            await Expect(fila.GetByRole(AriaRole.Menu)).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("Escape");
            await Expect(menu).ToHaveAttributeAsync("aria-expanded", "false");
            await Expect(menu).ToBeFocusedAsync();
        }

        // El modo de selección cambia geometría, sin marcar filas ni emitir comandos de datos.
        if (pantalla is "vehiculos" or "documentos")
        {
            var seleccion = page.Locator("header.cabecera-pagina").GetByRole(AriaRole.Button,
                new LocatorGetByRoleOptions { Name = "Selección múltiple", Exact = true });
            await Expect(seleccion).ToHaveCountAsync(1);
            await Expect(seleccion).ToHaveAttributeAsync("aria-pressed", "false");
            await seleccion.ClickAsync();
            await Expect(seleccion).ToHaveAttributeAsync("aria-pressed", "true");
            await Expect(tabla.Locator("thead .lp-col-seleccion")).ToHaveCountAsync(1);
            await ComprobarAsync(tabla, filas, pantalla);
            await CapturarAsync(page, pantalla, "seleccion");
        }
    }

    private async Task ElegirEmpresaSembradaAsync(IPage page)
    {
        var disparador = Ayudas.DisparadorSelectorTenant(page);
        if (await disparador.CountAsync() == 0)
            return; // La cuenta nativa conserva su Tenant propietario de origen.
        await Ayudas.AbrirSelectorTenantAsync(page);
        // Title procede del nombre real de la opción; no hay IDs/índices de catálogo.
        var opcion = page.Locator($".selector-tenant-panel [role=option][title=\"{Ayudas.NombreTenantOrigenPorDefecto}\"]");
        // La organización de origen se pinta en el grupo fijo, no en Recientes.
        await Expect(opcion).ToHaveCountAsync(1);
        var respuesta = await page.RunAndWaitForResponseAsync(() => opcion.ClickAsync(),
            r => r.Url.Contains("/cuenta/cliente-activo", StringComparison.Ordinal));
        Assert.True(respuesta.Status is >= 300 and < 400, "Control positivo: no se aplicó el contexto de la Empresa gestionada sembrada.");
        var destino = respuesta.Headers.GetValueOrDefault("location") ?? string.Empty;
        Assert.False(destino.Contains("acceso-denegado", StringComparison.Ordinal) || destino.Contains("iniciar-sesion", StringComparison.Ordinal),
            "Control positivo: el cambio de contexto fue rechazado.");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Ayudas.DisparadorSelectorTenant(page).Locator(".selector-tenant-nombre"))
            .ToHaveTextAsync(Ayudas.NombreTenantOrigenPorDefecto);
    }

    private static async Task ComprobarAsync(ILocator tabla, ILocator filas, string pantalla)
    {
        var columnas = pantalla switch
        {
            "vehiculos" => new[] { ("lp-col-vehiculo", 2d), ("lp-col-matricula", 1d), ("lp-col-empleador", 1.8), ("lp-col-documentacion", 1.2) },
            "documentos" => new[] { ("lp-col-entidad", 2d), ("lp-col-tipo", 1.8), ("lp-col-vigencia", 1.6), ("lp-col-estado", 1d), ("lp-col-plataformas", 1.3), ("lp-col-archivo", .9) },
            "gestiones" => new[] { ("lp-col-trabajador", 2d), ("lp-col-tipo", 1.8), ("lp-col-estado", 1.3), ("lp-col-creada", 1d), ("lp-col-completar", 1d) },
            _ => throw new ArgumentOutOfRangeException(nameof(pantalla))
        };
        var cabecera = await tabla.Locator("thead > tr").EvaluateAsync<FilaMedida>(MedirFilaJs);
        Aproximar(cabecera.Height, 38, $"{pantalla}: altura de cabecera");
        foreach (var celda in cabecera.Cells)
            Aproximar(celda.CssHeight, 38, $"{pantalla}: altura CSS efectiva de th");
        ComprobarColumnas(cabecera, columnas, pantalla);
        var medidas = await filas.EvaluateAllAsync<FilaMedida[]>("filas => filas.map(" + MedirFilaJs + ")");
        Assert.True(medidas.Length > 0, "Control positivo: desaparecieron las filas reales durante la medición.");
        foreach (var fila in medidas)
        {
            Assert.True(fila.Height >= 51.5, $"{pantalla}: fila real mide {fila.Height}px, inferior a 52px con tolerancia de borde0.5.");
            ComprobarColumnas(fila, columnas, pantalla);
            foreach (var celda in fila.Cells)
                Assert.True(celda.ContenidoDentro, $"{pantalla}: contenido desborda verticalmente {string.Join(' ', celda.Classes)}.");
            Assert.Single(fila.Menus);
            Aproximar(fila.Menus[0].Width, 30, $"{pantalla}: anchura del disparador de menú de fila");
            Aproximar(fila.Menus[0].Height, 30, $"{pantalla}: altura del disparador de menú de fila");
        }
    }

    private static void ComprobarColumnas(FilaMedida fila, (string Clase, double Peso)[] columnas, string pantalla)
    {
        var seleccion = fila.Cells.SingleOrDefault(c => c.Classes.Contains("lp-col-seleccion"));
        Assert.Equal(columnas.Length + 1 + (seleccion is null ? 0 : 1), fila.Cells.Length);
        var disponible = fila.Width - 32 - 12 * (columnas.Length + (seleccion is null ? 0 : 1)) - 40 - (seleccion is null ? 0 : 28);
        Assert.True(disponible > 0, "Control positivo: el ancho no permite aplicar el presupuesto de columnas.");
        var total = columnas.Sum(c => c.Peso);
        foreach (var (clase, peso) in columnas)
        {
            var celda = Assert.Single(fila.Cells, c => c.Classes.Contains(clase));
            var primera = ReferenceEquals(celda, fila.Cells[0]);
            Aproximar(celda.Width, peso * disponible / total + 12 + (primera ? 10 : 0), $"{pantalla}: ancho medido de {clase}");
            Aproximar(celda.PaddingLeft, primera ? 16 : 6, $"{pantalla}: padding izquierdo de {clase}");
            Aproximar(celda.PaddingRight, 6, $"{pantalla}: padding derecho de {clase}");
        }
        var menu = Assert.Single(fila.Cells, c => c.Classes.Contains("lp-col-menu"));
        Aproximar(menu.Width, 62, $"{pantalla}: columna menú");
        Aproximar(menu.PaddingLeft, 6, $"{pantalla}: padding izquierdo menú");
        Aproximar(menu.PaddingRight, 16, $"{pantalla}: padding derecho menú");
        if (seleccion is not null)
        {
            Aproximar(seleccion.Width, 50, $"{pantalla}: columna selección");
            Aproximar(seleccion.PaddingLeft, 16, $"{pantalla}: padding izquierdo selección");
            Aproximar(seleccion.PaddingRight, 6, $"{pantalla}: padding derecho selección");
        }
    }

    private static void Aproximar(double observado, double esperado, string motivo) =>
        Assert.True(Math.Abs(observado - esperado) <= 1,
            $"{motivo}: esperado {esperado:F3}px ±1px; observado {observado:F3}px.");

    private static async Task CapturarAsync(IPage page, string pantalla, string modo)
    {
        var directorio = Environment.GetEnvironmentVariable("HYDRA_VISUAL_DIR");
        if (string.IsNullOrWhiteSpace(directorio)) return;
        Directory.CreateDirectory(directorio);
        await page.ScreenshotAsync(new PageScreenshotOptions
        { Path = Path.Combine(directorio, $"{pantalla}-{modo}-{Guid.NewGuid():N}.png"), FullPage = true });
    }

    private const string MedirFilaJs = """
        fila => {
          const caja = fila.getBoundingClientRect();
          return { Width:caja.width, Height:caja.height,
            Cells:[...fila.children].filter(c => c.matches('th,td')).map(c => {
              const css=getComputedStyle(c);
              const rect=c.getBoundingClientRect();
              const dentro=[...c.children].every(h => {
                const r=h.getBoundingClientRect();
                return r.height===0 || (r.top>=rect.top-1 && r.bottom<=rect.bottom+1);
              });
              return {Classes:[...c.classList], ContenidoDentro:dentro, Width:rect.width, CssHeight:parseFloat(css.height),
                PaddingLeft:parseFloat(css.paddingLeft), PaddingRight:parseFloat(css.paddingRight)};
            }),
            Menus:[...fila.querySelectorAll('td.lp-col-menu .menu-acciones-disparador')].map(b => {
              const r=b.getBoundingClientRect();return {Width:r.width,Height:r.height};
            }) };
        }
        """;

    public sealed class FilaMedida
    {
        public double Width { get; set; }
        public double Height { get; set; }
        public CeldaMedida[] Cells { get; set; } = [];
        public Caja[] Menus { get; set; } = [];
    }
    public sealed class CeldaMedida
    {
        public string[] Classes { get; set; } = [];
        public double Width { get; set; }
        public double PaddingLeft { get; set; }
        public double PaddingRight { get; set; }
        public double CssHeight { get; set; }
        public bool ContenidoDentro { get; set; }
    }
    public sealed class Caja
    {
        public double Width { get; set; }
        public double Height { get; set; }
    }
}
