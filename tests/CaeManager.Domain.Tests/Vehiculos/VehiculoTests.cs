using CaeManager.Domain.Vehiculos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Vehiculos;

public class VehiculoTests
{
    [Fact]
    public void Crea_un_vehiculo_de_empresa_valido_y_normaliza_la_matricula_a_mayusculas()
    {
        var vehiculo = Vehiculo.DeEmpresa(Guid.NewGuid(), "Furgoneta 1", "Renault Kangoo", "1234abc");

        vehiculo.Nombre.Should().Be("Furgoneta 1");
        vehiculo.Modelo.Should().Be("Renault Kangoo");
        vehiculo.NumeroPlaca.Should().Be("1234ABC");
        vehiculo.EmpresaId.Should().NotBeNull();
        vehiculo.SubcontrataId.Should().BeNull();
        vehiculo.EsDeSubcontrata.Should().BeFalse();
    }

    [Fact]
    public void Crea_un_vehiculo_de_subcontrata_valido()
    {
        var vehiculo = Vehiculo.DeSubcontrata(Guid.NewGuid(), "Camion 1", "Iveco Daily", "5678XYZ");

        vehiculo.SubcontrataId.Should().NotBeNull();
        vehiculo.EmpresaId.Should().BeNull();
        vehiculo.EsDeSubcontrata.Should().BeTrue();
    }

    [Fact]
    public void Requiere_una_empresa_valida()
    {
        var accion = () => Vehiculo.DeEmpresa(Guid.Empty, "Furgoneta 1", "Renault Kangoo", "1234ABC");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Requiere_una_subcontrata_valida()
    {
        var accion = () => Vehiculo.DeSubcontrata(Guid.Empty, "Camion 1", "Iveco Daily", "5678XYZ");

        accion.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rechaza_un_nombre_vacio(string nombreInvalido)
    {
        var accion = () => Vehiculo.DeEmpresa(Guid.NewGuid(), nombreInvalido, "Renault Kangoo", "1234ABC");

        accion.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rechaza_una_matricula_vacia(string matriculaInvalida)
    {
        var accion = () => Vehiculo.DeEmpresa(Guid.NewGuid(), "Furgoneta 1", "Renault Kangoo", matriculaInvalida);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Actualizar_normaliza_y_reemplaza_nombre_modelo_y_matricula()
    {
        var vehiculo = Vehiculo.DeEmpresa(Guid.NewGuid(), "Furgoneta 1", "Renault Kangoo", "1234ABC");

        vehiculo.Actualizar("Furgoneta 2", "Renault Trafic", "9999xyz");

        vehiculo.Nombre.Should().Be("Furgoneta 2");
        vehiculo.Modelo.Should().Be("Renault Trafic");
        vehiculo.NumeroPlaca.Should().Be("9999XYZ");
    }
}
