using CaeManager.Application.Comunicaciones.Commands.VincularConversacion;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Comunicaciones;
using CaeManager.Domain.Comunicaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Comunicaciones.Commands.VincularConversacion;

public class VincularConversacionCommandHandlerTests
{
    [Fact]
    public async Task Fusiona_los_mensajes_del_origen_en_el_destino_y_cierra_el_origen()
    {
        var clienteId = Guid.NewGuid();
        var conversacionRepositorio = new ConversacionRepositorioFalso();

        var origen = Conversacion.CrearWhatsApp("+34600111222", Guid.NewGuid(), clienteId, Guid.NewGuid());
        var mensajeWhatsApp = origen.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.WhatsApp, "+34600111222", "Hola");
        conversacionRepositorio.Agregar(origen);

        var destino = new Conversacion("Consulta por correo", clienteId);
        conversacionRepositorio.Agregar(destino);

        var eventoRepositorio = new EventoConversacionRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new VincularConversacionCommandHandler(
            conversacionRepositorio, eventoRepositorio, new AlcanceDatosServiceFalso(), unitOfWork);

        var resultado = await handler.Handle(
            new VincularConversacionCommand(origen.Id, destino.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        destino.Mensajes.Should().Contain(m => m.Id == mensajeWhatsApp.Id);
        origen.Mensajes.Should().BeEmpty();
        origen.Estado.Should().Be(EstadoConversacion.Cerrada);
        eventoRepositorio.Eventos.Should().ContainSingle(e =>
            e.ConversacionId == destino.Id && e.Tipo == TipoEventoConversacion.ConversacionVinculada && e.ReferenciaId == origen.Id);
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Rechaza_vincular_una_conversacion_de_correo_como_origen()
    {
        var clienteId = Guid.NewGuid();
        var conversacionRepositorio = new ConversacionRepositorioFalso();

        var origenCorreo = new Conversacion("Consulta por correo", clienteId);
        conversacionRepositorio.Agregar(origenCorreo);
        var destino = new Conversacion("Otra consulta", clienteId);
        conversacionRepositorio.Agregar(destino);

        var unitOfWork = new UnitOfWorkFalso();
        var handler = new VincularConversacionCommandHandler(
            conversacionRepositorio, new EventoConversacionRepositorioFalso(), new AlcanceDatosServiceFalso(), unitOfWork);

        var resultado = await handler.Handle(
            new VincularConversacionCommand(origenCorreo.Id, destino.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Conversacion.CanalInvalido");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Rechaza_un_destino_de_otro_cliente()
    {
        var conversacionRepositorio = new ConversacionRepositorioFalso();

        var origen = Conversacion.CrearWhatsApp("+34600111222", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        conversacionRepositorio.Agregar(origen);

        var destinoDeOtroCliente = new Conversacion("Consulta de otro cliente", Guid.NewGuid());
        conversacionRepositorio.Agregar(destinoDeOtroCliente);

        var unitOfWork = new UnitOfWorkFalso();
        var handler = new VincularConversacionCommandHandler(
            conversacionRepositorio, new EventoConversacionRepositorioFalso(), new AlcanceDatosServiceFalso(), unitOfWork);

        var resultado = await handler.Handle(
            new VincularConversacionCommand(origen.Id, destinoDeOtroCliente.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Conversacion.DestinoNoEncontrado");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// Auditoría módulo 6: compartir Cliente no basta — si el destino cuelga
    /// de un buzón personal de otro gestor (ConexionIntegracionVisibleAsync
    /// en falso), la vinculación debe rechazarse igual que si no existiera.
    /// </summary>
    [Fact]
    public async Task Rechaza_un_destino_atado_a_un_buzon_personal_ajeno()
    {
        var clienteId = Guid.NewGuid();
        var conversacionRepositorio = new ConversacionRepositorioFalso();

        var origen = Conversacion.CrearWhatsApp("+34600111222", Guid.NewGuid(), clienteId, Guid.NewGuid());
        conversacionRepositorio.Agregar(origen);

        var conexionAjenaId = Guid.NewGuid();
        var destino = new Conversacion("Consulta por correo", clienteId);
        destino.AsociarConexion(conexionAjenaId, "hilo-externo-ajeno");
        conversacionRepositorio.Agregar(destino);

        var unitOfWork = new UnitOfWorkFalso();
        var handler = new VincularConversacionCommandHandler(
            conversacionRepositorio, new EventoConversacionRepositorioFalso(),
            new AlcanceDatosServiceFalso(conexionesIntegracionAjenas: [conexionAjenaId]), unitOfWork);

        var resultado = await handler.Handle(
            new VincularConversacionCommand(origen.Id, destino.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Conversacion.DestinoNoEncontrado");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Rechaza_un_destino_ya_cerrado()
    {
        var clienteId = Guid.NewGuid();
        var conversacionRepositorio = new ConversacionRepositorioFalso();

        var origen = Conversacion.CrearWhatsApp("+34600111222", Guid.NewGuid(), clienteId, Guid.NewGuid());
        conversacionRepositorio.Agregar(origen);

        var destinoCerrado = new Conversacion("Consulta ya resuelta", clienteId);
        destinoCerrado.CambiarEstado(EstadoConversacion.Cerrada);
        conversacionRepositorio.Agregar(destinoCerrado);

        var unitOfWork = new UnitOfWorkFalso();
        var handler = new VincularConversacionCommandHandler(
            conversacionRepositorio, new EventoConversacionRepositorioFalso(), new AlcanceDatosServiceFalso(), unitOfWork);

        var resultado = await handler.Handle(
            new VincularConversacionCommand(origen.Id, destinoCerrado.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Conversacion.DestinoNoDisponible");
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}
