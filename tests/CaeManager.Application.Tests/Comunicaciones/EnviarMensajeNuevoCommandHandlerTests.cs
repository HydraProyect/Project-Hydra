using CaeManager.Application.Comunicaciones.Commands.EnviarMensajeNuevo;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Comunicaciones;

public class EnviarMensajeNuevoCommandHandlerTests
{
    private static (EnviarMensajeNuevoCommandHandler Handler, ConversacionRepositorioFalso Conversaciones, Microsoft365GraphClientFalso GraphClient) CrearHandler(
        ConexionIntegracion conexion)
    {
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        credencialRepositorio.Agregar(new CredencialIntegracion(conexion.Id, "refresh-token"));
        var graphClient = new Microsoft365GraphClientFalso();
        var accesoGraph = new AccesoGraphService(credencialRepositorio, graphClient);
        var conversaciones = new ConversacionRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();

        var handler = new EnviarMensajeNuevoCommandHandler(
            conexionRepositorio, conversaciones, new AlcanceDatosServiceFalso(), graphClient, accesoGraph,
            new FileStorageServiceFalso(), unitOfWork);

        return (handler, conversaciones, graphClient);
    }

    [Fact]
    public async Task Crea_una_conversacion_nueva_con_el_mensaje_saliente()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");
        var (handler, conversaciones, _) = CrearHandler(conexion);

        var resultado = await handler.Handle(
            new EnviarMensajeNuevoCommand(conexion.Id, ["contacto@cliente.com"], "Bienvenida", "<p>Hola</p>"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var conversacion = conversaciones.Conversaciones.Should().ContainSingle().Which;
        conversacion.Id.Should().Be(resultado.Valor);
        conversacion.Asunto.Should().Be("Bienvenida");
        conversacion.Mensajes.Should().ContainSingle(m => m.Direccion == DireccionMensaje.Saliente && m.CuerpoHtml == "<p>Hola</p>");
        conversacion.Participantes.Should().ContainSingle(p => p.Email == "contacto@cliente.com" && p.Rol == RolParticipante.Para);
    }

    [Fact]
    public async Task Con_conexion_deshabilitada_devuelve_fallo_y_no_crea_nada()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");
        conexion.Deshabilitar();
        var (handler, conversaciones, _) = CrearHandler(conexion);

        var resultado = await handler.Handle(
            new EnviarMensajeNuevoCommand(conexion.Id, ["contacto@cliente.com"], "Bienvenida", "<p>Hola</p>"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ConexionIntegracion.NoDisponible");
        conversaciones.Conversaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Si_el_envio_por_Graph_falla_no_crea_la_conversacion()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");
        var (handler, conversaciones, graphClient) = CrearHandler(conexion);
        graphClient.FallaEnvio = true;

        var resultado = await handler.Handle(
            new EnviarMensajeNuevoCommand(conexion.Id, ["contacto@cliente.com"], "Bienvenida", "<p>Hola</p>"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        conversaciones.Conversaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Guarda_los_adjuntos_en_el_mensaje_creado()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");
        var (handler, conversaciones, _) = CrearHandler(conexion);
        var adjuntos = new[] { new AdjuntoParaEnviarDto("bienvenida.pdf", "application/pdf", [9, 9]) };

        var resultado = await handler.Handle(
            new EnviarMensajeNuevoCommand(conexion.Id, ["contacto@cliente.com"], "Bienvenida", "<p>Hola</p>", Adjuntos: adjuntos),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        conversaciones.Conversaciones.Single().Mensajes.Single().Adjuntos.Should()
            .ContainSingle(a => a.NombreArchivo == "bienvenida.pdf" && a.TamanoBytes == 2);
    }
}
