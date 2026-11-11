using Bunit;
using CaeManager.Web.Features.Documentos.Pages;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>La pantalla heredada /documentos/revision-ia pinta la barra única de la sección Documentos, no otra.</summary>
public partial class RevisionIaGen2Tests
{
    [Fact]
    public void La_pantalla_heredada_pinta_la_barra_unica_de_Documentos_con_Revision_IA_activa()
    {
        Registrar(new MediadorFalso { Revisiones = [Revision("Ana Ríos", 96, new DateOnly(2026, 9, 1), Guid.NewGuid())] });
        this.ConRolDeEscritura();

        var cut = Render<RevisionIa>();
        cut.WaitForAssertion(() => cut.FindAll(".revision-ia-cola").Should().NotBeEmpty());

        cut.FindAll("[role=tablist]").Should().ContainSingle("una sola tira de pestañas en la pantalla");
        BarraPestanasDocumentosTests.Rotulos(cut).Should().Equal(BarraPestanasDocumentosTests.SietePestanas);
        BarraPestanasDocumentosTests.Activa(cut).Should().Be("Revisión IA");
        cut.FindAll("nav.barra-seccion").Should().BeEmpty("la barra alternativa de sección ya no existe en esta pantalla");

        // Bajo el título de la pantalla, con la cola de revisión dentro del panel de la pestaña.
        var marcado = cut.Markup;
        var titulo = marcado.IndexOf("<h1", StringComparison.Ordinal);
        var tira = marcado.IndexOf("role=\"tablist\"", StringComparison.Ordinal);
        titulo.Should().BeGreaterThanOrEqualTo(0);
        tira.Should().BeGreaterThan(titulo, "la barra va bajo el título");
        cut.FindAll("[role=tabpanel] .revision-ia-cola").Should().NotBeEmpty("la cola de revisión vive dentro del panel de la pestaña");
    }
}
