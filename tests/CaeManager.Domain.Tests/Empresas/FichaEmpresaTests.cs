using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Empresas;

public class FichaEmpresaTests
{
    [Fact]
    public void La_ficha_normaliza_y_no_cambia_la_identidad_ni_las_relaciones()
    {
        var empresa = new Empresa("Empresa de prueba");
        empresa.ActualizarFicha(" DocuSign ", true, " Ana ", " Ajena ", " Prevención ", " Luis ", " 123 ", " spa@example.test ", " Mutua ", " Eva ", " 456 ", " formacion@example.test ", " Rosa ", " 789 ", " prl@example.test ");
        empresa.FirmanteDocumentos.Should().Be("Ana");
        empresa.ContactoSpaCorreo.Should().Be("spa@example.test");
        empresa.ContactoFormacionNombre.Should().Be("Eva");
        empresa.TecnicoPrlNombre.Should().Be("Rosa");
        empresa.SelloCustodiadoPorOperadorCae.Should().BeTrue();
        empresa.RazonSocial.Should().Be("Empresa de prueba");
        empresa.EsCritico.Should().BeNull();
    }

    [Fact]
    public void Valida_toda_la_ficha_antes_de_mutarla_y_permite_vaciarla()
    {
        var empresa = new Empresa("Empresa de prueba");
        var actualizar = () => empresa.ActualizarFicha("DocuSign", true, "Ana", null, null, null, null, null, null, null, null, null, null, null, new string('x', 255));
        actualizar.Should().Throw<ArgumentException>();
        empresa.FirmanteDocumentos.Should().BeNull();
        empresa.ActualizarFicha(null, null, " ", null, null, null, null, null, null, null, null, null, null, null, null);
        empresa.FirmanteDocumentos.Should().BeNull();
        empresa.SelloCustodiadoPorOperadorCae.Should().BeNull();
    }
}
