using Bunit;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Pie de lista con la leyenda de atajos (Cierre listados, línea H). Proyectos no maneja <c>x</c> (no hay
/// selección múltiple), así que su leyenda no la anuncia. No pagina: la leyenda va tras la tabla.
/// </summary>
public partial class ProyectosGen2Tests
{
    [Fact]
    public async Task Con_proyectos_el_pie_ensena_los_atajos_de_lista_sin_la_x_de_marcar()
    {
        _mediator.Proyectos = [ProyectoAbierto];
        var cut = await RenderizarConClienteAsync();

        cut.TeclasDeLaLeyenda().Should().Equal("j", "k", "Enter", "e", "f", "Alt", "?");
    }

    [Fact]
    public async Task Sin_proyectos_no_hay_leyenda_de_atajos()
    {
        var cut = await RenderizarConClienteAsync();

        cut.Find(".estado-vacio h3").TextContent.Should().Be("Sin proyectos", "es el estado vacío: no hay filas que recorrer");
        cut.FindAll(LeyendaAtajosEnPagina.Selector).Should().BeEmpty();
    }

    [Fact]
    public void Sin_Cliente_elegido_la_tabla_y_la_leyenda_se_pintan_con_todos_los_proyectos()
    {
        _mediator.Proyectos = [ProyectoAbierto];
        var cut = Renderizar();

        cut.FindAll(".tabla-proyectos").Should().NotBeEmpty("sin Cliente el filtro lista los Proyectos de todos los Clientes");
        cut.FindAll(LeyendaAtajosEnPagina.Selector).Should().NotBeEmpty("la leyenda acompaña a la tabla que se pinta");
    }
}
