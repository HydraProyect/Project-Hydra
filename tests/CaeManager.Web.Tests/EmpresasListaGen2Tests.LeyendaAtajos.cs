using Bunit;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Pie de lista con la leyenda de atajos (Cierre listados, línea H). Empresas maneja todos los atajos de
/// lista, así que su leyenda no omite ninguno.
/// </summary>
public partial class EmpresasListaGen2Tests
{
    [Fact]
    public void Con_empresas_el_pie_ensena_todos_los_atajos_de_lista_aunque_no_haya_paginador()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Empresa("Montajes Ebro S.L.") } });

        cut.TeclasDeLaLeyenda().Should().Equal("j", "k", "Enter", "e", "x", "f", "Alt", "?");
        cut.FindAll(".paginador-simple").Should().BeEmpty("con una sola página no hay paginador, y la leyenda no depende de él");
    }

    [Fact]
    public void Sin_empresas_no_hay_leyenda_de_atajos()
    {
        var cut = Renderizar(new MediatorFalso());

        cut.Markup.Should().Contain("Aún no hay empresas", "es el estado vacío: no hay filas que recorrer");
        cut.FindAll(LeyendaAtajosEnPagina.Selector).Should().BeEmpty();
    }
}
