using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Attribute splatting (P2 #28 de docs/business/MATURITY_REVIEW.md): un
/// atributo HTML que Boton no reconoce como [Parameter] debe llegar igual
/// al &lt;button&gt; renderizado, sin que haga falta añadirle un parámetro
/// nuevo para cada caso (aria-label, data-testid...).
/// </summary>
public class BotonTests : BunitContext
{
    [Fact]
    public void Un_atributo_no_reconocido_llega_al_boton_renderizado()
    {
        var cut = Render<Boton>(parametros => parametros
            .AddUnmatched("aria-label", "Cerrar panel")
            .AddUnmatched("data-testid", "boton-cerrar"));

        var boton = cut.Find("button");

        boton.GetAttribute("aria-label").Should().Be("Cerrar panel");
        boton.GetAttribute("data-testid").Should().Be("boton-cerrar");
    }

    [Fact]
    public void Las_clases_propias_del_boton_se_conservan_junto_a_las_del_llamador()
    {
        var cut = Render<Boton>(parametros => parametros
            .AddUnmatched("class", "clase-extra"));

        var boton = cut.Find("button");

        boton.GetAttribute("class").Should().Contain("boton").And.Contain("clase-extra");
    }
}
