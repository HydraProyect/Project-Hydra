using CaeManager.Domain.Subcontratas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Subcontratas;

public class SubcontrataTests
{
    private const string CifValido = "B12345674";

    [Fact]
    public void Crea_una_subcontrata_valida()
    {
        var subcontrata = new Subcontrata("Limpiezas Ejemplo S.L.");

        subcontrata.RazonSocial.Should().Be("Limpiezas Ejemplo S.L.");
        subcontrata.Cif.Should().BeNull();
    }

    [Fact]
    public void Crea_una_subcontrata_con_cif_valido()
    {
        var subcontrata = new Subcontrata("Limpiezas Ejemplo S.L.", CifValido);

        subcontrata.Cif.Should().Be(CifValido);
    }

    [Fact]
    public void Recorta_espacios_en_la_razon_social()
    {
        var subcontrata = new Subcontrata("  Limpiezas Ejemplo S.L.  ");

        subcontrata.RazonSocial.Should().Be("Limpiezas Ejemplo S.L.");
    }

    [Fact]
    public void Rechaza_una_razon_social_vacia()
    {
        var accion = () => new Subcontrata("   ");

        accion.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("B12345670")] // formato de CIF, dígito de control incorrecto
    [InlineData("77189989A")] // formato de DNI, no de CIF de empresa
    public void No_permite_un_cif_invalido_si_se_proporciona(string cifInvalido)
    {
        var accion = () => new Subcontrata("Limpiezas Ejemplo S.L.", cifInvalido);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Actualizar_cambia_la_razon_social()
    {
        var subcontrata = new Subcontrata("Nombre original");

        subcontrata.Actualizar("Nombre nuevo", cif: null);

        subcontrata.RazonSocial.Should().Be("Nombre nuevo");
    }

    [Fact]
    public void Actualizar_permite_quitar_el_cif()
    {
        var subcontrata = new Subcontrata("Limpiezas Ejemplo S.L.", CifValido);

        subcontrata.Actualizar("Limpiezas Ejemplo S.L.", cif: null);

        subcontrata.Cif.Should().BeNull();
    }

    [Fact]
    public void Actualizar_normaliza_el_cif_a_mayusculas()
    {
        var subcontrata = new Subcontrata("Limpiezas Ejemplo S.L.");

        subcontrata.Actualizar("Limpiezas Ejemplo S.L.", cif: CifValido.ToLowerInvariant());

        subcontrata.Cif.Should().Be(CifValido);
    }
}
