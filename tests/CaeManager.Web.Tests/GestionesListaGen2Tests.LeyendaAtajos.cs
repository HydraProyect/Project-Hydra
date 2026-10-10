using Bunit;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Pie de lista con la leyenda de atajos (Cierre listados, línea H). Gestiones no maneja <c>x</c> (no hay
/// selección múltiple) ni <c>e</c> (no hay edición desde la vista rápida): su leyenda no las anuncia.
/// </summary>
public partial class GestionesListaGen2Tests
{
    [Fact]
    public void Con_gestiones_el_pie_ensena_los_atajos_de_lista_sin_marcar_ni_editar()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Gestion("Juan Pérez Ibarra") } });

        cut.TeclasDeLaLeyenda().Should().Equal("j", "k", "Enter", "f", "Alt", "?");
    }

    [Fact]
    public void Sin_gestiones_no_hay_leyenda_de_atajos()
    {
        var cut = Renderizar(new MediatorFalso());

        cut.FindAll(".estado-vacio").Should().ContainSingle("es el estado vacío: no hay filas que recorrer");
        cut.FindAll(LeyendaAtajosEnPagina.Selector).Should().BeEmpty();
    }
}
