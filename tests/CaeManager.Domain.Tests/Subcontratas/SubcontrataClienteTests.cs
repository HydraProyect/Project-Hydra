using CaeManager.Domain.Subcontratas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Subcontratas;

public class SubcontrataClienteTests
{
    [Fact]
    public void Crea_una_asociacion_valida()
    {
        var subcontrataId = Guid.NewGuid();
        var clienteId = Guid.NewGuid();

        var asociacion = new SubcontrataCliente(subcontrataId, clienteId);

        asociacion.SubcontrataId.Should().Be(subcontrataId);
        asociacion.ClienteId.Should().Be(clienteId);
    }

    [Fact]
    public void Requiere_una_subcontrata_valida()
    {
        var accion = () => new SubcontrataCliente(Guid.Empty, Guid.NewGuid());

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Requiere_un_cliente_valido()
    {
        var accion = () => new SubcontrataCliente(Guid.NewGuid(), Guid.Empty);

        accion.Should().Throw<ArgumentException>();
    }
}
