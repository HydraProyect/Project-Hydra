using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Tests;

/// <summary>
/// <see cref="CajasFicha"/>: las cajas de la pestaña «Ficha» de una página 360.
/// <para>
/// Lo que estos tests observan: que cada caja se pinta con su clave, que con dos o más el
/// componente pide sus alturas al navegador y las recoloca de la más alta a la más baja, que
/// el empate conserva el orden de llegada, que con una sola caja no mide y que un conjunto
/// de cajas distinto se vuelve a medir. Lo que NO observan: las dos columnas ni que no queden
/// huecos; eso lo pinta el CSS y se mide en el navegador.
/// </para>
/// </summary>
public class CajasFichaTests : BunitContext
{
    private const string Modulo = "./js/cajas-ficha.js";

    private static CajaFicha Caja(string clave) => new(clave, b => b.AddContent(0, clave));

    private static IEnumerable<string?> Claves(IRenderedComponent<CajasFicha> cut) =>
        cut.FindAll("[data-pieza=cajas-ficha] > [data-caja]").Select(c => c.GetAttribute("data-caja"));

    [Fact]
    public void Las_cajas_se_recolocan_de_la_mas_alta_a_la_mas_baja()
    {
        var modulo = JSInterop.SetupModule(Modulo);
        modulo.Setup<double[]>("medirAlturas", _ => true).SetResult([40, 120, 80]);

        var cut = Render<CajasFicha>(p => p.Add(x => x.Cajas, [Caja("baja"), Caja("alta"), Caja("media")]));

        cut.WaitForAssertion(() => Claves(cut).Should().Equal("alta", "media", "baja"));
        modulo.Invocations["medirAlturas"].Should().ContainSingle("el orden se calcula una vez por conjunto de cajas");
    }

    [Fact]
    public void Dos_cajas_de_la_misma_altura_conservan_su_orden_de_llegada()
    {
        JSInterop.SetupModule(Modulo).Setup<double[]>("medirAlturas", _ => true).SetResult([60, 90, 60]);

        var cut = Render<CajasFicha>(p => p.Add(x => x.Cajas, [Caja("primera"), Caja("alta"), Caja("segunda")]));

        cut.WaitForAssertion(() => Claves(cut).Should().Equal("alta", "primera", "segunda"));
    }

    [Fact]
    public void Con_una_sola_caja_no_hay_nada_que_ordenar_y_no_se_mide()
    {
        // Modo estricto y sin módulo preparado: cualquier llamada al navegador rompería el test.
        var cut = Render<CajasFicha>(p => p.Add(x => x.Cajas, [Caja("plazo")]));

        Claves(cut).Should().Equal("plazo");
        cut.Find("[data-caja=plazo]").TextContent.Should().Be("plazo");
        JSInterop.Invocations.Should().BeEmpty();
    }

    [Fact]
    public void Un_conjunto_de_cajas_distinto_se_vuelve_a_medir()
    {
        var modulo = JSInterop.SetupModule(Modulo);
        var medida = modulo.Setup<double[]>("medirAlturas", _ => true);
        medida.SetResult([10, 20]);

        var cut = Render<CajasFicha>(p => p.Add(x => x.Cajas, [Caja("a"), Caja("b")]));
        cut.WaitForAssertion(() => Claves(cut).Should().Equal("b", "a"));

        // Llega una tercera caja: se pinta al final (b, a, c) y esa es la medida que se pide.
        medida.SetResult([10, 20, 30]);
        cut.Render(p => p.Add(x => x.Cajas, [Caja("a"), Caja("b"), Caja("c")]));

        cut.WaitForAssertion(() => Claves(cut).Should().Equal("c", "a", "b"));
        modulo.Invocations["medirAlturas"].Should().HaveCount(2);
    }
}
