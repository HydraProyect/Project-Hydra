using CaeManager.Application.Clientes.Commands.EliminarCliente;
using CaeManager.Domain.Clientes;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Clientes;

public class EliminarClienteCommandHandlerTests
{
    [Fact]
    public async Task Marca_el_cliente_como_eliminado_cuando_no_tiene_centros_activos()
    {
        var cliente = new Cliente("Bebidas del Norte S.A. (Planta El Prat)", "B12345674", true);
        var repositorio = new ClienteRepositorioFalso { TieneCentrosActivos = false };
        repositorio.Agregar(cliente);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarClienteCommandHandler(repositorio, unitOfWork);

        var resultado = await handler.Handle(new EliminarClienteCommand(cliente.Id, Guid.NewGuid()), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        cliente.EstaEliminado.Should().BeTrue();
    }

    [Fact]
    public async Task Falla_cuando_el_cliente_tiene_centros_activos()
    {
        var cliente = new Cliente("Bebidas del Norte S.A. (Planta El Prat)", "B12345674", true);
        var repositorio = new ClienteRepositorioFalso { TieneCentrosActivos = true };
        repositorio.Agregar(cliente);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarClienteCommandHandler(repositorio, unitOfWork);

        var resultado = await handler.Handle(new EliminarClienteCommand(cliente.Id, Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Cliente.TieneCentrosActivos");
        cliente.EstaEliminado.Should().BeFalse();
    }

    [Fact]
    public async Task Falla_cuando_el_cliente_no_existe()
    {
        var repositorio = new ClienteRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarClienteCommandHandler(repositorio, unitOfWork);

        var resultado = await handler.Handle(new EliminarClienteCommand(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Cliente.NoEncontrado");
    }
}
