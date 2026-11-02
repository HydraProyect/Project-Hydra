using System.Text.Json;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// MJ-11: lo que <see cref="AccesibilidadAxeOutboundE2ETests"/> no cubre. Mismo ratchet de dos sentidos
/// (empeorar o mejorar sin anotar rompe el test) en dos mediciones:
/// <list type="bullet">
/// <item><description>Objetivos táctiles de 24 px (WCAG 2.5.8, regla <c>target-size</c> de axe) en las mismas rutas de
/// <see cref="AccesibilidadAxeOutboundE2ETests.Rutas"/>. La regla no va en el conjunto por defecto de axe, así que se
/// pide con <c>runOnly</c>. Línea base: <c>axe-linea-base-objetivos-24px-outbound.json</c>.</description></item>
/// <item><description>Superficies que no son una ruta con sesión de Gestor CAE: inicio de sesión (sin sesión), el
/// buscador global abierto y un Drawer abierto. Todas las reglas, como en el ratchet de rutas. Línea base:
/// <c>axe-linea-base-superficies-outbound.json</c>.</description></item>
/// </list>
/// Un rojo inicial no se oculta: se congela en la línea base con su número, y quien lo arregle lo baja en la misma PR.
/// </summary>
[Collection("AppCollectionGestorCaeCarteraMultiTenant")]
public class AccesibilidadAxeOutboundSuperficiesE2ETests(WebAppFixtureGestorCaeCarteraMultiTenant fixture)
{
    private const string EmailGestorCae = "gestor.cartera.e2e@caemanager.local";
    private const string TenantBeneficiarioA = "Conservas Albatros S.L. (Tenant beneficiario E2E)";
    private const string FicheroObjetivos = "axe-linea-base-objetivos-24px-outbound.json";
    private const string FicheroSuperficies = "axe-linea-base-superficies-outbound.json";

    private static readonly LocatorAssertionsToBeVisibleOptions EsperaEnFrio = new() { Timeout = 30_000 };

    public static TheoryData<string> RutasAnalizadas() => [.. AccesibilidadAxeOutboundE2ETests.Rutas];

    [Fact]
    public void Las_lineas_base_no_contienen_rutas_que_ya_no_se_analizan()
    {
        var rutas = LeerTodo(FicheroObjetivos).Keys;
        Assert.Empty(rutas.Except(AccesibilidadAxeOutboundE2ETests.Rutas));
    }

    [Theory]
    [MemberData(nameof(RutasAnalizadas))]
    public async Task Los_objetivos_de_24_px_de_la_pantalla_Outbound_no_empeoran_ni_mejoran_sin_anotar_la_linea_base(string ruta)
    {
        await using var contexto = await NuevoContextoAsync();
        var page = await contexto.NewPageAsync();
        await EntrarComoGestorCaeAsync(page);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}{ruta}");
        Assert.Equal(ruta, new Uri(page.Url).AbsolutePath);
        await Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToBeVisibleAsync(EsperaEnFrio);
        await Expect(page.Locator("[class*=esqueleto]")).ToHaveCountAsync(0, new() { Timeout = 30_000 });

        var resultado = await page.RunAxe(new AxeRunOptions
        {
            RunOnly = new RunOnlyOptions { Type = "rule", Values = ["target-size"] },
        });

        ComprobarContraLineaBase(FicheroObjetivos, ruta, resultado);
    }

    [Fact]
    public async Task El_inicio_de_sesion_no_empeora_ni_mejora_sin_anotar_la_linea_base_de_axe()
    {
        await using var contexto = await NuevoContextoAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/cuenta/iniciar-sesion");
        await Expect(page.Locator("#email")).ToBeVisibleAsync(EsperaEnFrio);

        ComprobarContraLineaBase(FicheroSuperficies, "inicio-de-sesion", await page.RunAxe());
    }

    [Fact]
    public async Task El_buscador_global_abierto_no_empeora_ni_mejora_sin_anotar_la_linea_base_de_axe()
    {
        await using var contexto = await NuevoContextoAsync();
        var page = await contexto.NewPageAsync();
        await EntrarComoGestorCaeAsync(page);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/mi-trabajo");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToBeVisibleAsync(EsperaEnFrio);

        await page.Keyboard.PressAsync("Control+k");
        await Expect(page.GetByRole(AriaRole.Dialog, new() { Name = "Buscar o ejecutar" })).ToBeVisibleAsync(EsperaEnFrio);

        ComprobarContraLineaBase(FicheroSuperficies, "buscador-global-abierto", await page.RunAxe());
    }

    [Fact]
    public async Task Un_Drawer_abierto_no_empeora_ni_mejora_sin_anotar_la_linea_base_de_axe()
    {
        await using var contexto = await NuevoContextoAsync();
        var page = await contexto.NewPageAsync();
        await EntrarComoGestorCaeAsync(page);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/empresas");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToBeVisibleAsync(EsperaEnFrio);

        // Sin localizador posicional: si esta cuenta no ofrece un único «+ Nueva empresa» el test falla y se elige otro Drawer.
        await page.GetByRole(AriaRole.Button, new() { Name = "+ Nueva empresa", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Dialog)).ToBeVisibleAsync(EsperaEnFrio);

        ComprobarContraLineaBase(FicheroSuperficies, "drawer-abierto-empresas", await page.RunAxe());
    }

    private Task<IBrowserContext> NuevoContextoAsync() =>
        fixture.Browser.NewContextAsync(new() { BypassCSP = true, ReducedMotion = ReducedMotion.Reduce });

    private async Task EntrarComoGestorCaeAsync(IPage page)
    {
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, EmailGestorCae, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.CambiarClienteActivoAsync(page, fixture.BaseUrl, TenantBeneficiarioA);
    }

    private static void ComprobarContraLineaBase(string fichero, string clave, AxeResult resultado)
    {
        // axe tiene que haber evaluado algo (pasa, falla o queda incompleto); de lo contrario el ratchet no observa nada.
        Assert.True(resultado.Passes.Length + resultado.Violations.Length + resultado.Incomplete.Length > 0,
            $"{clave}: axe no evaluó ningún nodo; el ratchet no observa nada.");
        var actual = resultado.Violations.ToDictionary(v => v.Id, v => v.Nodes.Length);
        var baseline = LeerTodo(fichero).GetValueOrDefault(clave) ?? [];

        var empeoran = actual.Where(a => a.Value > baseline.GetValueOrDefault(a.Key)).ToList();
        var mejoran = baseline.Where(b => actual.GetValueOrDefault(b.Key) < b.Value).ToList();
        var mensaje = new System.Text.StringBuilder();
        foreach (var e in empeoran)
        {
            var detalle = resultado.Violations.First(v => v.Id == e.Key);
            mensaje.AppendLine($"EMPEORA {e.Key} ({detalle.Impact}): {baseline.GetValueOrDefault(e.Key)} -> {e.Value} nodos. {detalle.Help}");
            foreach (var nodo in detalle.Nodes.Take(8))
                mensaje.AppendLine($"    {nodo.Target}");
        }
        foreach (var m in mejoran)
            mensaje.AppendLine($"MEJORA {m.Key}: {m.Value} -> {actual.GetValueOrDefault(m.Key)} nodos; baja la línea base a ese valor.");

        Assert.True(empeoran.Count == 0 && mejoran.Count == 0,
            $"{clave} ({fichero}): axe se ha movido respecto a la línea base.\n{mensaje}\nValor actual para la línea base: "
            + JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, int>> { [clave] = actual.OrderBy(a => a.Key).ToDictionary(a => a.Key, a => a.Value) }));
    }

    private static Dictionary<string, Dictionary<string, int>> LeerTodo(string fichero) =>
        JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Accesibilidad", fichero)))
        ?? throw new InvalidOperationException($"Línea base de axe ilegible: {fichero}.");
}
