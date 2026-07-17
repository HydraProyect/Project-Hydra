using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Empresas;

public class EmpresaClienteTests
{
    [Fact]
    public void Crea_una_asociacion_valida()
    {
        var empresaId = Guid.NewGuid();
        var clienteId = Guid.NewGuid();

        var asociacion = new EmpresaCliente(empresaId, clienteId);

        asociacion.EmpresaId.Should().Be(empresaId);
        asociacion.ClienteId.Should().Be(clienteId);
    }

    [Fact]
    public void Requiere_una_empresa_valida()
    {
        var accion = () => new EmpresaCliente(Guid.Empty, Guid.NewGuid());

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Requiere_un_cliente_valido()
    {
        var accion = () => new EmpresaCliente(Guid.NewGuid(), Guid.Empty);

        accion.Should().Throw<ArgumentException>();
    }
}
