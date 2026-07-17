using CaeManager.Application.Clientes.Commands.CrearCliente;
using CaeManager.Domain.Clientes;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Clientes;

public class CrearClienteCommandHandlerTests
{
    private const string CifValido = "B12345674";
    private const string OtroCifValido = "P1234567D";

    [Fact]
    public async Task Crea_el_cliente_y_guarda_cuando_la_razon_social_no_esta_repetida()
    {
        var repositorio = new ClienteRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new CrearClienteCommandHandler(repositorio, unitOfWork);

        var resultado = await handler.Handle(
            new CrearClienteCommand("COBEGA (Coca-Cola European Partners)", CifValido, true, "Notas"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        repositorio.Clientes.Should().ContainSingle(c => c.RazonSocial == "COBEGA (Coca-Cola European Partners)");
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Falla_cuando_ya_existe_un_cliente_con_la_misma_razon_social()
    {
        var repositorio = new ClienteRepositorioFalso();
        repositorio.Agregar(new Cliente("RENDELSUR", CifValido, false));
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new CrearClienteCommandHandler(repositorio, unitOfWork);

        var resultado = await handler.Handle(new CrearClienteCommand("RENDELSUR", OtroCifValido, false, null), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Cliente.RazonSocialDuplicada");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Falla_cuando_ya_existe_un_cliente_con_el_mismo_cif()
    {
        var repositorio = new ClienteRepositorioFalso();
        repositorio.Agregar(new Cliente("RENDELSUR", CifValido, false));
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new CrearClienteCommandHandler(repositorio, unitOfWork);

        var resultado = await handler.Handle(new CrearClienteCommand("Otra razón social", CifValido, false, null), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Cliente.CifDuplicado");
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}
