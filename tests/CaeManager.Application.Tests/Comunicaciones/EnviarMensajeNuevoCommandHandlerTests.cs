using CaeManager.Application.Comunicaciones.Commands.EnviarMensajeNuevo;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
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
            new FileStorageServiceFalso(), new ResolucionParticipanteConversacionServiceFalso(), unitOfWork);

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
        conversacion.ConexionIntegracionId.Should().Be(conexion.Id);
        conversacion.HiloExternoId.Should().Be("hilo-ext-falso-456");
        var mensaje = conversacion.Mensajes.Should().ContainSingle(m => m.Direccion == DireccionMensaje.Saliente && m.CuerpoHtml == "<p>Hola</p>").Which;
        mensaje.MensajeExternoId.Should().Be("msg-ext-falso-123");
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

    [Fact]
    public async Task Un_entrante_con_el_mismo_hilo_externo_se_suma_al_hilo_existente_en_vez_de_crear_uno_nuevo()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        credencialRepositorio.Agregar(new CredencialIntegracion(conexion.Id, "refresh-token"));
        var graphClient = new Microsoft365GraphClientFalso
        {
            MensajeEnviadoADevolver = new MensajeEnviadoGraphDto("msg-saliente-1", "hilo-compartido-123")
        };
        var accesoGraph = new AccesoGraphService(credencialRepositorio, graphClient);
        var conversaciones = new ConversacionRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();

        var handler = new EnviarMensajeNuevoCommandHandler(
            conexionRepositorio, conversaciones, new AlcanceDatosServiceFalso(), graphClient, accesoGraph,
            new FileStorageServiceFalso(), new ResolucionParticipanteConversacionServiceFalso(), unitOfWork);

        // 1. Enviar mensaje saliente
        var resultadoEnvio = await handler.Handle(
            new EnviarMensajeNuevoCommand(conexion.Id, ["contacto@cliente.com"], "Consulta CAE", "<p>Mensaje inicial</p>"),
            CancellationToken.None);
        resultadoEnvio.EsExitoso.Should().BeTrue();

        var conversacionOriginal = conversaciones.Conversaciones.Should().ContainSingle().Which;
        conversacionOriginal.ConexionIntegracionId.Should().Be(conexion.Id);
        conversacionOriginal.HiloExternoId.Should().Be("hilo-compartido-123");

        // 2. Simular respuesta entrante del cliente con el mismo HiloExternoId
        graphClient.MensajeIdsADevolver = ["msg-entrante-2"];
        graphClient.MensajeADevolver = new MensajeGraphDto(
            "msg-entrante-2", "hilo-compartido-123", "Re: Consulta CAE", "contacto@cliente.com", "<p>Respuesta cliente</p>",
            DateTime.UtcNow, [new ParticipanteGraphDto("contacto@cliente.com", RolParticipante.De)], []);

        var servicioIngesta = new IngestaWebhookService(
            conexionRepositorio, conversaciones, graphClient, accesoGraph,
            new FileStorageServiceFalso(),
            new SugerenciaVisitaCorreoServiceFalso(),
            new SugerenciaGestionCorreoServiceFalso(),
            new ResolucionParticipanteConversacionServiceFalso(),
            new ResolucionProveedorPlataformaCaeServiceFalso(),
            new ClasificacionRuidoMensajeRepositorioFalso(),
            new ClasificacionRuidoMensajeServiceFalso(),
            new RelevanciaCaeServiceFalso(),
            NullLogger<IngestaWebhookService>.Instance);

        var eventoWebhook = new EventoWebhook(conexion.Id, "{\"value\":[{}]}");
        await servicioIngesta.ProcesarAsync(eventoWebhook, CancellationToken.None);

        // 3. Afirmar que NO se ha creado una conversación nueva y que la respuesta se ha sumado al hilo
        conversaciones.Conversaciones.Should().HaveCount(1);
        conversacionOriginal.Mensajes.Should().HaveCount(2);
        conversacionOriginal.Mensajes.Should().ContainSingle(m => m.Direccion == DireccionMensaje.Saliente && m.MensajeExternoId == "msg-saliente-1");
        conversacionOriginal.Mensajes.Should().ContainSingle(m => m.Direccion == DireccionMensaje.Entrante && m.MensajeExternoId == "msg-entrante-2");
    }

    [Fact]
    public async Task Si_Graph_devuelve_un_HiloExternoId_existente_reutiliza_la_conversacion_en_vez_de_duplicarla()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");
        var (handler, conversaciones, graphClient) = CrearHandler(conexion);
        graphClient.MensajeEnviadoADevolver = new MensajeEnviadoGraphDto("msg-saliente-2", "hilo-existente-456");

        var conversacionPrevia = new Conversacion("Conversación anterior");
        conversacionPrevia.AsociarConexion(conexion.Id, "hilo-existente-456");
        conversaciones.Agregar(conversacionPrevia);

        var resultado = await handler.Handle(
            new EnviarMensajeNuevoCommand(conexion.Id, ["contacto@cliente.com"], "Nuevo envío", "<p>Segunda parte</p>"),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().Be(conversacionPrevia.Id);
        conversaciones.Conversaciones.Should().ContainSingle();
        conversacionPrevia.Mensajes.Should().ContainSingle(m => m.MensajeExternoId == "msg-saliente-2");
    }
}
