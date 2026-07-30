using CaeManager.Application.Comunicaciones.Commands.ResponderConversacion;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Comunicaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Comunicaciones;

public class ResponderConversacionCommandHandlerTests
{
    [Fact]
    public async Task Agrega_mensaje_saliente_y_actualiza_fecha_ultimo_mensaje()
    {
        var conversacion = new ConversacionCorreo("Duda sobre vigencia documental", clienteId: Guid.NewGuid());
        var repositorio = new ConversacionCorreoRepositorioFalso();
        repositorio.Agregar(conversacion);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new ResponderConversacionCommandHandler(repositorio, new AlcanceDatosServiceFalso(), unitOfWork);

        var resultado = await handler.Handle(
            new ResponderConversacionCommand(conversacion.Id, "<p>Ya está todo en regla.</p>"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        conversacion.Mensajes.Should().ContainSingle(m =>
            m.Direccion == DireccionMensaje.Saliente && m.CuerpoHtml == "<p>Ya está todo en regla.</p>");
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Devuelve_fallo_si_la_conversacion_no_existe()
    {
        var repositorio = new ConversacionCorreoRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new ResponderConversacionCommandHandler(repositorio, new AlcanceDatosServiceFalso(), unitOfWork);

        var resultado = await handler.Handle(
            new ResponderConversacionCommand(Guid.NewGuid(), "<p>Hola</p>"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ConversacionCorreo.NoEncontrada");
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}
