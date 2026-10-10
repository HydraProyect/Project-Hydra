using Bunit;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Pie de lista con la leyenda de atajos (Cierre listados, línea H). Documentos no maneja <c>e</c> (no hay
/// edición desde la vista rápida), así que su leyenda no la anuncia.
/// </summary>
public partial class DocumentosGen2Tests
{
    [Fact]
    public void Con_documentos_el_pie_ensena_los_atajos_de_lista_sin_la_e_de_editar()
    {
        var (cut, _) = Renderizar(ConDocumentos(Documento("Contrato con leyenda")));

        cut.WaitForAssertion(() => cut.TeclasDeLaLeyenda().Should().Equal("j", "k", "Enter", "x", "f", "Alt", "?"));
        cut.FindAll(".paginador-simple").Should().BeEmpty("con una sola página no hay paginador, y la leyenda no depende de él");
    }

    [Fact]
    public void Sin_documentos_no_hay_leyenda_de_atajos()
    {
        var (cut, _) = Renderizar();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Aún no hay documentos", "es el estado vacío: no hay filas que recorrer"));
        cut.FindAll(LeyendaAtajosEnPagina.Selector).Should().BeEmpty();
    }
}
