using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Empresas;

public class EmpresaTests
{
    private const string CifValido = "B12345674";

    [Fact]
    public void Crea_una_empresa_sin_cif()
    {
        var empresa = new Empresa("Limpiezas del Norte S.L.");

        empresa.RazonSocial.Should().Be("Limpiezas del Norte S.L.");
        empresa.Cif.Should().BeNull();
        empresa.EstaEliminado.Should().BeFalse();
    }

    [Fact]
    public void Crea_una_empresa_con_cif_valido()
    {
        var empresa = new Empresa("Limpiezas del Norte S.L.", CifValido);

        empresa.Cif.Should().Be(CifValido);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void No_permite_crear_una_empresa_sin_razon_social(string razonSocialInvalida)
    {
        var accion = () => new Empresa(razonSocialInvalida, CifValido);

        accion.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("B12345670")] // formato de CIF, dígito de control incorrecto
    [InlineData("77189989A")] // formato de DNI, no de CIF de empresa
    public void No_permite_un_cif_invalido_si_se_proporciona(string cifInvalido)
    {
        var accion = () => new Empresa("Limpiezas del Norte S.L.", cifInvalido);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Actualizar_permite_quitar_el_cif()
    {
        var empresa = new Empresa("Limpiezas del Norte S.L.", CifValido);

        empresa.Actualizar("Limpiezas del Norte S.L.", cif: null);

        empresa.Cif.Should().BeNull();
    }

    [Fact]
    public void Actualizar_normaliza_el_cif_a_mayusculas()
    {
        var empresa = new Empresa("Limpiezas del Norte S.L.");

        empresa.Actualizar("Limpiezas del Norte S.L.", cif: CifValido.ToLowerInvariant());

        empresa.Cif.Should().Be(CifValido);
    }
}
