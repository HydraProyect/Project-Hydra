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
        cut.FindAll(".pie-lista-maqueta").Should().ContainSingle("el pie permanece visible con una sola página");
        cut.FindAll(".pie-lista-maqueta button").Should().OnlyContain(b => b.HasAttribute("disabled"));
    }

    [Fact]
    public void Sin_documentos_el_pie_conserva_la_leyenda_de_atajos()
    {
        var (cut, _) = Renderizar();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Todavía no hay documentos", "es el estado vacío: no hay filas que recorrer"));
        cut.FindAll(LeyendaAtajosEnPagina.Selector).Should().ContainSingle("el pie conserva la leyenda también sin resultados");
    }
}
