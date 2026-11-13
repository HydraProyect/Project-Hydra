using Bunit;
using CaeManager.Web.Features.Documentos.Pages;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// D-49: la pantalla /documentos/revision-ia ya no lleva la barra de pestañas de Documentos (se retira en la PR8 de la
/// serie): es una pantalla propia con revisión en lateral y un enlace de vuelta al listado.
/// </summary>
public partial class RevisionIaGen2Tests
{
    [Fact]
    public void La_pantalla_de_revision_ia_es_propia_con_revision_en_lateral_y_vuelta_al_listado()
    {
        Registrar(new MediadorFalso { Revisiones = [Revision("Ana Ríos", 96, new DateOnly(2026, 9, 1), Guid.NewGuid())] });
        this.ConRolDeEscritura();

        var cut = Render<RevisionIa>();
        cut.WaitForAssertion(() => cut.FindAll(".revision-ia-lateral").Should().NotBeEmpty());

        cut.FindAll("[role=tablist]").Should().BeEmpty("la pantalla ya no lleva la barra de pestañas de Documentos");
        cut.FindAll("nav.barra-seccion").Should().BeEmpty();
        cut.FindAll("h1").Should().ContainSingle().Which.TextContent.Should().Contain("Revisión IA");
        cut.FindAll("a.revision-ia-volver").Should().ContainSingle().Which.GetAttribute("href").Should().Be("/documentos");
        cut.FindAll(".revision-ia-pdf").Should().NotBeEmpty("el documento va a la izquierda, junto al lateral");
    }
}
