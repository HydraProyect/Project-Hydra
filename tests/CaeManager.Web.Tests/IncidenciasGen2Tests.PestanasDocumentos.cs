using Bunit;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>Incidencias es una pestaña de la sección Documentos: pinta la barra única de la sección, y no otra.</summary>
public partial class IncidenciasGen2Tests
{
    [Fact]
    public void Incidencias_pinta_la_barra_unica_de_Documentos_bajo_el_titulo_con_Incidencias_activa()
    {
        var cut = Renderizar(new MediadorControlado { Filas = [Fila(IdAlfa, "Centro Alfa")] });
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().NotBeEmpty());

        cut.FindAll("[role=tablist]").Should().ContainSingle("una sola tira de pestañas en la pantalla");
        BarraPestanasDocumentosTests.Rotulos(cut).Should().Equal(BarraPestanasDocumentosTests.SietePestanas);
        BarraPestanasDocumentosTests.Activa(cut).Should().Be("Incidencias");
        cut.FindAll("nav.barra-seccion").Should().BeEmpty("la barra alternativa de sección ya no existe en esta pantalla");

        // Misma posición que en Documentos: bajo el título y las acciones de la página, encima de filtros y rejilla.
        var marcado = cut.Markup;
        var titulo = marcado.IndexOf("<h1", StringComparison.Ordinal);
        var tira = marcado.IndexOf("role=\"tablist\"", StringComparison.Ordinal);
        var filas = marcado.IndexOf("<tbody", StringComparison.Ordinal);
        titulo.Should().BeGreaterThanOrEqualTo(0);
        tira.Should().BeGreaterThan(titulo, "la barra va bajo el título");
        filas.Should().BeGreaterThan(tira, "la lista de incidencias vive dentro del panel de la pestaña");
    }
}
