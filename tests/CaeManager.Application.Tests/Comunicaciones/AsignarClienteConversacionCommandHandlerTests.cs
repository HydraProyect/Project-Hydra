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

        var conversacion = new Conversacion("Correo sin cliente identificado");
        var conversacionRepositorio = new ConversacionRepositorioFalso();
        conversacionRepositorio.Agregar(conversacion);

        var unitOfWork = new UnitOfWorkFalso();
        var handler = new AsignarClienteConversacionCommandHandler(conversacionRepositorio, clienteRepositorio, new ContactoWhatsAppRepositorioFalso(), new AlcanceDatosServiceFalso(), unitOfWork);

        var resultado = await handler.Handle(
            new AsignarClienteConversacionCommand(conversacion.Id, cliente.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        conversacion.ClienteId.Should().Be(cliente.Id);
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Asignar_cliente_a_una_conversacion_whatsapp_memoriza_el_telefono_en_el_catalogo()
    {
        var cliente = new Cliente("Cadena Industrial Iberia", "B12345674", false);
        var clienteRepositorio = new ClienteRepositorioFalso();
        clienteRepositorio.Agregar(cliente);

        var conversacion = Conversacion.CrearWhatsApp("+34600111222", Guid.NewGuid(), null, Guid.NewGuid());
        var conversacionRepositorio = new ConversacionRepositorioFalso();
        conversacionRepositorio.Agregar(conversacion);

        var contactos = new ContactoWhatsAppRepositorioFalso();
        var handler = new AsignarClienteConversacionCommandHandler(
            conversacionRepositorio, clienteRepositorio, contactos, new AlcanceDatosServiceFalso(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(
            new AsignarClienteConversacionCommand(conversacion.Id, cliente.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        contactos.Contactos.Should().ContainSingle(c => c.Telefono == "+34600111222" && c.ClienteId == cliente.Id);
    }

    [Fact]
    public async Task Rechaza_un_cliente_inexistente_sin_tocar_la_conversacion()
    {
        var conversacion = new Conversacion("Correo sin cliente identificado");
        var conversacionRepositorio = new ConversacionRepositorioFalso();
        conversacionRepositorio.Agregar(conversacion);

        var clienteRepositorio = new ClienteRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new AsignarClienteConversacionCommandHandler(conversacionRepositorio, clienteRepositorio, new ContactoWhatsAppRepositorioFalso(), new AlcanceDatosServiceFalso(), unitOfWork);

        var resultado = await handler.Handle(
            new AsignarClienteConversacionCommand(conversacion.Id, Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Cliente.NoEncontrado");
        conversacion.ClienteId.Should().BeNull();
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}
