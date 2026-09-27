using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>P1-E2b: la instantánea con la que cada formulario decide su «hay cambios sin guardar».</summary>
public class InstantaneaFormularioTests
{
    [Fact]
    public void Sin_fijar_nunca_difiere()
    {
        var instantanea = new InstantaneaFormulario();

        instantanea.Difiere("algo escrito").Should().BeFalse("un formulario que no se ha abierto no tiene nada que perder");
    }

    [Fact]
    public void Difiere_cuando_cambia_un_valor_y_deja_de_diferir_al_volver_al_original()
    {
        var instantanea = new InstantaneaFormulario();
        instantanea.Fijar("Norte", null, false, new DateOnly(2026, 9, 26));

        instantanea.Difiere("Norte", null, false, new DateOnly(2026, 9, 26)).Should().BeFalse();
        instantanea.Difiere("Norte", "B12345678", false, new DateOnly(2026, 9, 26)).Should().BeTrue();
        instantanea.Difiere("Norte", null, true, new DateOnly(2026, 9, 26)).Should().BeTrue();
        instantanea.Difiere("Norte", null, false, new DateOnly(2026, 9, 27)).Should().BeTrue();
    }

    [Fact]
    public void Nulo_y_vacio_cuentan_igual()
    {
        var instantanea = new InstantaneaFormulario();
        instantanea.Fijar(null, "x");

        instantanea.Difiere(string.Empty, "x").Should().BeFalse("borrar lo preseleccionado en blanco no es un cambio distinto de no tocarlo");
    }

    [Fact]
    public void Los_valores_no_se_confunden_al_desplazarse_entre_campos()
    {
        var instantanea = new InstantaneaFormulario();
        instantanea.Fijar("ab", "c");

        instantanea.Difiere("a", "bc").Should().BeTrue();
    }

    [Fact]
    public void Las_colecciones_se_comparan_como_conjunto()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var instantanea = new InstantaneaFormulario();
        instantanea.Fijar(new HashSet<Guid> { a, b });

        instantanea.Difiere(new List<Guid> { b, a }).Should().BeFalse("el orden de marcado no es un cambio");
        instantanea.Difiere(new List<Guid> { a }).Should().BeTrue();
    }

}
