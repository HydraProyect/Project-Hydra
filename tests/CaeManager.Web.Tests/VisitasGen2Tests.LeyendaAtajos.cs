using Bunit;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Pie de lista con la leyenda de atajos (Cierre listados, línea H). Visitas no monta <c>BarraFiltros</c>,
/// la única que pinta el campo «Filtrar esta pantalla» al que salta <c>f</c>: aquí esa tecla no hace nada y
/// la leyenda no la anuncia.
/// </summary>
public partial class VisitasGen2Tests
{
    [Fact]
    public void Con_visitas_el_pie_ensena_los_atajos_de_lista_sin_la_f_de_filtrar()
    {
        var cut = Renderizar(new MediatorVisitas { Visitas = { Visita("Centro Norte") } });

        cut.WaitForAssertion(() => cut.TeclasDeLaLeyenda().Should().Equal("j", "k", "Enter", "e", "x", "Alt", "?"));
        cut.FindAll("[data-filtro-pantalla]").Should().BeEmpty("sin ese campo, «f» no tiene adónde ir");
    }

    [Fact]
    public void Sin_visitas_no_hay_leyenda_de_atajos()
    {
        var cut = Renderizar(new MediatorVisitas());

        cut.WaitForAssertion(() => cut.FindAll(".estado-vacio").Should().ContainSingle("es el estado vacío: no hay filas que recorrer"));
        cut.FindAll(LeyendaAtajosEnPagina.Selector).Should().BeEmpty();
    }
}
