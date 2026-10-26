using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// Attribute splatting (P2 #28 de Project-Hydra-Negocio/MATURITY_REVIEW.md): un
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

    // CWE-602 («validación solo en el cliente»): en Blazor Server, quien quita `disabled` en el DOM del navegador consigue que el
    // clic llegue al servidor. El estado deshabilitado (o cargando) es del servidor y el clic no sale hacia el llamador.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Un_boton_deshabilitado_o_cargando_no_llama_a_OnClick_aunque_el_clic_llegue_sin_el_atributo_disabled(bool deshabilitado, bool cargando)
    {
        var clics = 0;
        var cut = Render<Boton>(parametros => parametros
            .Add(x => x.Variante, VarianteBoton.Secundario)
            .Add(x => x.Deshabilitado, deshabilitado)
            .Add(x => x.Cargando, cargando)
            .Add(x => x.OnClick, EventCallback.Factory.Create(this, () => clics++)));
        var boton = cut.Find("button");
        boton.HasAttribute("disabled").Should().BeTrue();

        boton.RemoveAttribute("disabled");
        await boton.ClickAsync(new MouseEventArgs());

        clics.Should().Be(0);
    }

    [Fact]
    public async Task Un_boton_habilitado_llama_a_OnClick()
    {
        var clics = 0;
        var cut = Render<Boton>(parametros => parametros
            .Add(x => x.Variante, VarianteBoton.Secundario)
            .Add(x => x.OnClick, EventCallback.Factory.Create(this, () => clics++)));

        await cut.Find("button").ClickAsync(new MouseEventArgs());

        clics.Should().Be(1);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task VentanaContextoElemento_solo_avisa_al_llamador_si_esta_habilitado(bool deshabilitado, int esperados)
    {
        var avisos = 0;
        var cut = Render<VentanaContextoElemento>(parametros => parametros
            .AddChildContent("Incidencia")
            .Add(x => x.Deshabilitado, deshabilitado)
            .Add(x => x.AlPulsar, EventCallback.Factory.Create(this, () => avisos++)));
        var elemento = cut.Find("button");
        elemento.HasAttribute("disabled").Should().Be(deshabilitado);

        elemento.RemoveAttribute("disabled");
        await elemento.ClickAsync(new MouseEventArgs());

        avisos.Should().Be(esperados);
    }

    [Fact]
    public void Sin_OnClick_el_boton_no_registra_manejador_de_clic()
    {
        var cut = Render<Boton>(parametros => parametros.Add(x => x.Variante, VarianteBoton.Secundario));

        cut.Markup.Should().NotContain("blazor:onclick", "un botón sin OnClick (de envío de formulario, por ejemplo) no manda clics de balde al circuito");
    }
}
