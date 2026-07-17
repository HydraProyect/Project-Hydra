using CaeManager.Domain.Centros;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Centros;

public class CentroTests
{
    [Fact]
    public void Crea_un_centro_valido()
    {
        var clienteId = Guid.NewGuid();
        var empresaId = Guid.NewGuid();

        var centro = new Centro(clienteId, empresaId, "Planta Zaragoza");

        centro.ClienteId.Should().Be(clienteId);
        centro.EmpresaId.Should().Be(empresaId);
        centro.Nombre.Should().Be("Planta Zaragoza");
    }

    [Fact]
    public void Requiere_un_cliente_valido()
    {
        var accion = () => new Centro(Guid.Empty, Guid.NewGuid(), "Planta Zaragoza");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Requiere_una_empresa_valida()
    {
        var accion = () => new Centro(Guid.NewGuid(), Guid.Empty, "Planta Zaragoza");

        accion.Should().Throw<ArgumentException>();
    }
}
