using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.ResponderConversacion;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Application.Tests.Comunicaciones;

public class ResponderConversacionCommandHandlerTests
{
    private static ResponderConversacionCommandHandler CrearHandler(
        ConversacionRepositorioFalso repositorio, UnitOfWorkFalso unitOfWork, bool permitirRemitenteSimulado = false)
    {
        var graphClient = new Microsoft365GraphClientFalso();
        var accesoGraph = new AccesoGraphService(new CredencialIntegracionRepositorioFalso(), graphClient);
        var opciones = Options.Create(new ComunicacionesRemitenteOptions { PermitirRemitenteSimulado = permitirRemitenteSimulado });
        return new ResponderConversacionCommandHandler(
            repositorio, new ConexionIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(), graphClient, accesoGraph,
            new FileStorageServiceFalso(), opciones, unitOfWork);
    }

    private static ResponderConversacionCommandHandler CrearHandlerConConexion(
        ConversacionRepositorioFalso repositorio, ConexionIntegracion conexion, UnitOfWorkFalso unitOfWork,
        Microsoft365GraphClientFalso? graphClient = null)
    {
        graphClient ??= new Microsoft365GraphClientFalso();
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        credencialRepositorio.Agregar(new CredencialIntegracion(conexion.Id, "refresh-token"));
        var accesoGraph = new AccesoGraphService(credencialRepositorio, graphClient);
        var opciones = Options.Create(new ComunicacionesRemitenteOptions());
        return new ResponderConversacionCommandHandler(
            repositorio, conexionRepositorio, new AlcanceDatosServiceFalso(), graphClient, accesoGraph,
            new FileStorageServiceFalso(), opciones, unitOfWork);
    }

    [Fact]
    public async Task Sin_conexion_y_sin_permitir_remitente_simulado_devuelve_fallo_y_no_persiste_nada()
    {
        var conversacion = new Conversacion("Duda sobre vigencia documental", clienteId: Guid.NewGuid());
        var repositorio = new ConversacionRepositorioFalso();
        repositorio.Agregar(conversacion);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(repositorio, unitOfWork);

        var resultado = await handler.Handle(
            new ResponderConversacionCommand(conversacion.Id, "<p>Ya está todo en regla.</p>"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Conversacion.SinBuzonConectado");
        conversacion.Mensajes.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Con_remitente_simulado_permitido_agrega_mensaje_saliente_y_actualiza_fecha_ultimo_mensaje()
    {
        var conversacion = new Conversacion("Duda sobre vigencia documental", clienteId: Guid.NewGuid());
        var repositorio = new ConversacionRepositorioFalso();
        repositorio.Agregar(conversacion);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(repositorio, unitOfWork, permitirRemitenteSimulado: true);

        var resultado = await handler.Handle(
            new ResponderConversacionCommand(conversacion.Id, "<p>Ya está todo en regla.</p>"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        conversacion.Mensajes.Should().ContainSingle(m =>
            m.Direccion == DireccionMensaje.Saliente && m.CuerpoHtml == "<p>Ya está todo en regla.</p>");
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Con_remitente_simulado_permitido_guarda_los_adjuntos_en_el_mensaje_saliente()
    {
        var conversacion = new Conversacion("Duda sobre vigencia documental", clienteId: Guid.NewGuid());
        var repositorio = new ConversacionRepositorioFalso();
        repositorio.Agregar(conversacion);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(repositorio, unitOfWork, permitirRemitenteSimulado: true);
        var adjuntos = new[] { new AdjuntoParaEnviarDto("formato-centro.pdf", "application/pdf", [1, 2, 3]) };

        var resultado = await handler.Handle(
            new ResponderConversacionCommand(conversacion.Id, "<p>Adjunto el formato.</p>", adjuntos), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var mensaje = conversacion.Mensajes.Should().ContainSingle().Which;
        mensaje.Adjuntos.Should().ContainSingle(a => a.NombreArchivo == "formato-centro.pdf" && a.TamanoBytes == 3);
    }

    [Fact]
    public async Task Devuelve_fallo_si_la_conversacion_no_existe()
    {
        var repositorio = new ConversacionRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(repositorio, unitOfWork);

        var resultado = await handler.Handle(
            new ResponderConversacionCommand(Guid.NewGuid(), "<p>Hola</p>"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Conversacion.NoEncontrada");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Con_conexion_real_envia_por_Graph_y_persiste_el_mensaje_sin_mensajeExternoId()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");
        var conversacion = new Conversacion("Duda sobre vigencia documental");
        conversacion.AsociarConexion(conexion.Id, "graph-thread-1");
        conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@ejemplo.com", "<p>hola</p>", mensajeExternoId: "graph-msg-1");
        var repositorio = new ConversacionRepositorioFalso();
        repositorio.Agregar(conversacion);
        var unitOfWork = new UnitOfWorkFalso();
        var graphClient = new Microsoft365GraphClientFalso();
        var handler = CrearHandlerConConexion(repositorio, conexion, unitOfWork, graphClient);

        var resultado = await handler.Handle(
            new ResponderConversacionCommand(conversacion.Id, "<p>Respuesta real</p>"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        graphClient.UltimoMensajeExternoIdRespondido.Should().Be("graph-msg-1");
        conversacion.Mensajes.Should().Contain(m =>
            m.Direccion == DireccionMensaje.Saliente && m.Remitente == "cae@cliente.com" && m.MensajeExternoId == null);
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Con_conexion_deshabilitada_devuelve_fallo_y_no_persiste_nada()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");
        conexion.Deshabilitar();
        var conversacion = new Conversacion("Duda sobre vigencia documental");
        conversacion.AsociarConexion(conexion.Id, "graph-thread-1");
        conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@ejemplo.com", "<p>hola</p>", mensajeExternoId: "graph-msg-1");
        var repositorio = new ConversacionRepositorioFalso();
        repositorio.Agregar(conversacion);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandlerConConexion(repositorio, conexion, unitOfWork);

        var resultado = await handler.Handle(
            new ResponderConversacionCommand(conversacion.Id, "<p>Respuesta real</p>"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Conversacion.ConexionNoDisponible");
        conversacion.Mensajes.Should().HaveCount(1);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Sin_mensaje_entrante_previo_devuelve_fallo()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");
        var conversacion = new Conversacion("Duda sobre vigencia documental");
        conversacion.AsociarConexion(conexion.Id, "graph-thread-1");
        var repositorio = new ConversacionRepositorioFalso();
        repositorio.Agregar(conversacion);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandlerConConexion(repositorio, conexion, unitOfWork);

        var resultado = await handler.Handle(
            new ResponderConversacionCommand(conversacion.Id, "<p>Respuesta real</p>"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Conversacion.SinMensajeOrigen");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Si_el_envio_por_Graph_falla_no_persiste_un_mensaje_fantasma()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");
        var conversacion = new Conversacion("Duda sobre vigencia documental");
        conversacion.AsociarConexion(conexion.Id, "graph-thread-1");
        conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@ejemplo.com", "<p>hola</p>", mensajeExternoId: "graph-msg-1");
        var repositorio = new ConversacionRepositorioFalso();
        repositorio.Agregar(conversacion);
        var unitOfWork = new UnitOfWorkFalso();
        var graphClient = new Microsoft365GraphClientFalso { FallaEnvio = true };
        var handler = CrearHandlerConConexion(repositorio, conexion, unitOfWork, graphClient);

        var resultado = await handler.Handle(
            new ResponderConversacionCommand(conversacion.Id, "<p>Respuesta real</p>"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        conversacion.Mensajes.Should().HaveCount(1);
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}
