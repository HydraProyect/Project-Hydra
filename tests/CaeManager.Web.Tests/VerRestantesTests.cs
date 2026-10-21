using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>Pie «Ver los N restantes» de una fila desplegada con muchos elementos: enseña los primeros y pide el resto.</summary>
public class VerRestantesTests : BunitContext
{
    public VerRestantesTests() => Services.AddLocalization();

    [Fact]
    public void Con_mas_de_diez_elementos_el_pie_dice_cuantos_quedan()
    {
        var cut = Render<VerRestantes>(p => p.Add(c => c.Total, 300));

        cut.Find("button.ver-restantes-fila").TextContent.Trim().Should().Be("Ver los 290 restantes");
    }

    [Fact]
    public void Hasta_diez_elementos_no_hay_pie_y_tampoco_cuando_ya_se_pidieron_todos()
    {
        Render<VerRestantes>(p => p.Add(c => c.Total, VerRestantes.Inicial)).FindAll("button").Should().BeEmpty();
        Render<VerRestantes>(p => p.Add(c => c.Total, 300).Add(c => c.Completa, true)).FindAll("button").Should().BeEmpty();
    }

    [Fact]
    public async Task Pulsar_el_pie_avisa_a_la_pantalla()
    {
        var pulsado = false;
        var cut = Render<VerRestantes>(p => p.Add(c => c.Total, 12).Add(c => c.OnVerRestantes, () => pulsado = true));

        await cut.Find("button.ver-restantes-fila").ClickAsync(new MouseEventArgs());

        pulsado.Should().BeTrue();
    }

    [Fact]
    public void Recortar_ensena_los_diez_primeros_o_todos_si_se_pidieron()
    {
        var elementos = Enumerable.Range(1, 300).ToList();

        VerRestantes.Recortar(elementos, completa: false).Should().Equal(Enumerable.Range(1, 10));
        VerRestantes.Recortar(elementos, completa: true).Should().HaveCount(300);
    }

    [Fact]
    public void Con_uno_solo_de_mas_se_enseñan_todos_y_no_hay_pie()
    {
        VerRestantes.Recortar(Enumerable.Range(1, 11).ToList(), completa: false).Should().HaveCount(11);
        Render<VerRestantes>(p => p.Add(c => c.Total, 11)).FindAll("button").Should().BeEmpty();
    }
}
