using System.Text.Json;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// M-42 (Ola 2, accesibilidad): axe-core como ratchet sobre las pantallas del flujo Outbound, entrando
/// COMO Gestor CAE de un Operador CAE externo con cartera (la misma siembra que
/// <see cref="GestorCaeCarteraMultiTenantTests"/>). Se ejecutan TODAS las reglas de axe, sin
/// <c>WithTags</c> ni <c>DisableRules</c>: el listón no se baja filtrando reglas.
///
/// <para>
/// La línea base (<c>Accesibilidad/axe-linea-base-outbound.json</c>) guarda, por ruta, cuántos nodos
/// incumplen cada regla hoy. Es un ratchet de dos sentidos: un nodo nuevo o una regla nueva rompen el
/// test, y también lo rompe una mejora que no se haya anotado en la línea base — así el listón solo
/// sube. Quien arregle un defecto baja el número en el mismo PR; nunca se sube para conseguir un verde.
/// </para>
///
/// <para>
/// Se crea el contexto con <c>BypassCSP</c> solo porque axe se inyecta en la página; la CSP de la
/// aplicación no se toca ni se mide aquí.
/// </para>
/// </summary>
[Collection("AppCollectionGestorCaeCarteraMultiTenant")]
public class AccesibilidadAxeOutboundE2ETests(WebAppFixtureGestorCaeCarteraMultiTenant fixture)
{
    private const string EmailGestorCae = "gestor.cartera.e2e@caemanager.local";
    private const string TenantBeneficiarioA = "Conservas Albatros S.L. (Tenant beneficiario E2E)";

    private static readonly LocatorAssertionsToBeVisibleOptions EsperaEnFrio = new() { Timeout = 30_000 };

    [Theory]
    [InlineData("/mi-trabajo")]
    [InlineData("/trabajadores")]
    [InlineData("/centros")]
    [InlineData("/documentos")]
    [InlineData("/visitas")]
    public async Task La_pantalla_Outbound_no_empeora_ni_mejora_sin_anotar_la_linea_base_de_axe(string ruta)
    {
        await using var contexto = await fixture.Browser.NewContextAsync(new() { BypassCSP = true });
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, EmailGestorCae, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.CambiarClienteActivoAsync(page, fixture.BaseUrl, TenantBeneficiarioA);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}{ruta}");
        // Una pantalla sin contenido de aplicación no se analiza: sería una medición vacía que pasaría por vacía.
        await Expect(page.GetByRole(AriaRole.Main)).ToBeVisibleAsync(EsperaEnFrio);

        var resultado = await page.RunAxe();

        // axe tiene que haber evaluado reglas en esta pantalla; de lo contrario el ratchet no observa nada.
        Assert.NotEmpty(resultado.Passes);
        var actual = resultado.Violations.ToDictionary(v => v.Id, v => v.Nodes.Length);
        var baseline = LeerLineaBase(ruta);

        var empeoran = actual.Where(a => a.Value > baseline.GetValueOrDefault(a.Key)).ToList();
        var mejoran = baseline.Where(b => actual.GetValueOrDefault(b.Key) < b.Value).ToList();
        var mensaje = new System.Text.StringBuilder();
        foreach (var e in empeoran)
        {
            var detalle = resultado.Violations.First(v => v.Id == e.Key);
            mensaje.AppendLine($"EMPEORA {e.Key} ({detalle.Impact}): {baseline.GetValueOrDefault(e.Key)} -> {e.Value} nodos. {detalle.Help}");
            foreach (var nodo in detalle.Nodes.Take(5))
                mensaje.AppendLine($"    {nodo.Target}");
        }
        foreach (var m in mejoran)
            mensaje.AppendLine($"MEJORA {m.Key}: {m.Value} -> {actual.GetValueOrDefault(m.Key)} nodos; baja la línea base a ese valor.");

        Assert.True(empeoran.Count == 0 && mejoran.Count == 0,
            $"Ruta {ruta}: axe se ha movido respecto a la línea base.\n{mensaje}\nValor actual para la línea base: "
            + JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, int>> { [ruta] = actual.OrderBy(a => a.Key).ToDictionary(a => a.Key, a => a.Value) }));
    }

    private static Dictionary<string, int> LeerLineaBase(string ruta)
    {
        var fichero = Path.Combine(AppContext.BaseDirectory, "Accesibilidad", "axe-linea-base-outbound.json");
        var todo = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(File.ReadAllText(fichero))
                   ?? throw new InvalidOperationException("Línea base de axe ilegible.");
        return todo.GetValueOrDefault(ruta) ?? [];
    }
}
