using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Tests;

public sealed class TarjetaListadoTests : BunitContext
{
    [Fact]
    public void Cabecera_grupo_y_fila_comparten_tarjeta_con_el_pie()
    {
        var cut = Render<TarjetaListado>(p => p
            .Add(c => c.Contenido, "<div class='cabecera'>Centro</div><div class='grupo'>Cliente empresarial</div><div class='fila'>Almacén</div>")
            .Add(c => c.Pie, "<div class='paginador'>Página 1</div><ul class='leyenda'><li>Atajos</li></ul>"));

        cut.FindAll(".tarjeta-listado").Should().ContainSingle();
        cut.FindAll(".tarjeta-listado > .tarjeta-listado-contenido > div").Should().HaveCount(3);
        cut.FindAll(".tarjeta-listado > .tarjeta-listado-pie > *").Should().HaveCount(2);
        cut.Find(".tarjeta-listado-contenido").NextElementSibling!.ClassList.Should().Contain("tarjeta-listado-pie");
    }

    [Fact]
    public void Sin_pie_no_crea_una_franja_vacia()
    {
        var cut = Render<TarjetaListado>(p => p.Add(c => c.Contenido, "<p>Contenido</p>"));

        cut.FindAll(".tarjeta-listado-pie").Should().BeEmpty();
        cut.Find(".tarjeta-listado-contenido p").TextContent.Should().Be("Contenido");
    }

    [Fact]
    public async Task El_envoltorio_conserva_los_eventos_del_contenido_y_del_pie()
    {
        var pulsaciones = new List<string>();
        RenderFragment Boton(string nombre) => builder =>
        {
            builder.OpenElement(0, "button");
            builder.AddAttribute(1, "onclick", EventCallback.Factory.Create(this, () => pulsaciones.Add(nombre)));
            builder.AddContent(2, nombre);
            builder.CloseElement();
        };
        var cut = Render<TarjetaListado>(p => p.Add(c => c.Contenido, Boton("Fila")).Add(c => c.Pie, Boton("Siguiente")));

        await cut.Find(".tarjeta-listado-contenido button").ClickAsync(new());
        await cut.Find(".tarjeta-listado-pie button").ClickAsync(new());

        pulsaciones.Should().Equal("Fila", "Siguiente");
    }
}
