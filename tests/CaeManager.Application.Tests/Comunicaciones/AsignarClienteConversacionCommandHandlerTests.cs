using CaeManager.Application.Comunicaciones.Commands.AsignarClienteConversacion;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Comunicaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Comunicaciones;

public class AsignarClienteConversacionCommandHandlerTests
{
    [Fact]
    public async Task Asigna_el_cliente_a_una_conversacion_de_triage()
    {
        var cliente = new Cliente("Cadena Industrial Iberia", "B12345674", false);
        var clienteRepositorio = new ClienteRepositorioFalso();
        clienteRepositorio.Agregar(cliente);

        var conversacion = new ConversacionCorreo("Correo sin cliente identificado");
        var conversacionRepositorio = new ConversacionCorreoRepositorioFalso();
        conversacionRepositorio.Agregar(conversacion);

        var unitOfWork = new UnitOfWorkFalso();
        var handler = new AsignarClienteConversacionCommandHandler(conversacionRepositorio, clienteRepositorio, new AlcanceDatosServiceFalso(), unitOfWork);

        var resultado = await handler.Handle(
            new AsignarClienteConversacionCommand(conversacion.Id, cliente.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        conversacion.ClienteId.Should().Be(cliente.Id);
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Rechaza_un_cliente_inexistente_sin_tocar_la_conversacion()
    {
        var conversacion = new ConversacionCorreo("Correo sin cliente identificado");
        var conversacionRepositorio = new ConversacionCorreoRepositorioFalso();
        conversacionRepositorio.Agregar(conversacion);

        var clienteRepositorio = new ClienteRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new AsignarClienteConversacionCommandHandler(conversacionRepositorio, clienteRepositorio, new AlcanceDatosServiceFalso(), unitOfWork);

        var resultado = await handler.Handle(
            new AsignarClienteConversacionCommand(conversacion.Id, Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Cliente.NoEncontrado");
        conversacion.ClienteId.Should().BeNull();
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}
