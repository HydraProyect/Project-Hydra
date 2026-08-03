using CaeManager.Application.Integraciones;
using CaeManager.Application.Tests.Comunicaciones;
using CaeManager.Application.Tests.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

public class IngestaWebhookServiceTests
{
    private static IngestaWebhookService CrearServicio(
        ConexionIntegracionRepositorioFalso conexionRepositorio,
        ConversacionCorreoRepositorioFalso conversacionRepositorio,
        Microsoft365GraphClientFalso graphClient,
        CredencialIntegracionRepositorioFalso? credencialRepositorio = null) =>
        new(conexionRepositorio, conversacionRepositorio, graphClient,
            new AccesoGraphService(credencialRepositorio ?? new CredencialIntegracionRepositorioFalso(), graphClient),
            new FileStorageServiceFalso(),
            new SugerenciaVisitaCorreoServiceFalso(),
            new SugerenciaGestionCorreoServiceFalso(),
            NullLogger<IngestaWebhookService>.Instance);

    private static ConexionIntegracion ConexionHabilitada(Guid? clienteId = null)
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE", clienteId);
        return conexion;
    }

    [Fact]
    public async Task Crea_una_conversacion_nueva_para_un_hilo_no_visto_antes()
    {
        var clienteId = Guid.NewGuid();
        var conexion = ConexionHabilitada(clienteId);
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var conversacionRepositorio = new ConversacionCorreoRepositorioFalso();
        var graphClient = new Microsoft365GraphClientFalso
        {
            MensajeIdsADevolver = ["graph-msg-1"],
            MensajeADevolver = new MensajeGraphDto(
                "graph-msg-1", "graph-thread-1", "Duda sobre CAE", "cliente@ejemplo.com", "<p>hola</p>", DateTime.UtcNow,
                [new ParticipanteGraphDto("cliente@ejemplo.com", RolParticipante.De)], []),
        };
        var credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        credencialRepositorio.Agregar(new CredencialIntegracion(conexion.Id, "refresh-token"));
        var servicio = CrearServicio(conexionRepositorio, conversacionRepositorio, graphClient, credencialRepositorio);
        var evento = new EventoWebhook(conexion.Id, "{\"value\":[{}]}");

        await servicio.ProcesarAsync(evento, CancellationToken.None);

        evento.Procesado.Should().BeTrue();
        evento.ErrorProcesado.Should().BeNull();
        conversacionRepositorio.Conversaciones.Should().ContainSingle();
        var conversacion = conversacionRepositorio.Conversaciones[0];
        conversacion.HiloExternoId.Should().Be("graph-thread-1");
        conversacion.ClienteId.Should().Be(clienteId);
        conversacion.Mensajes.Should().ContainSingle(m => m.MensajeExternoId == "graph-msg-1");
    }

    [Fact]
    public async Task Reutiliza_la_conversacion_existente_del_mismo_hilo_externo()
    {
        var conexion = ConexionHabilitada();
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var conversacionExistente = new ConversacionCorreo("Duda sobre CAE");
        conversacionExistente.AsociarConexion(conexion.Id, "graph-thread-1");
        var conversacionRepositorio = new ConversacionCorreoRepositorioFalso();
        conversacionRepositorio.Agregar(conversacionExistente);
        var graphClient = new Microsoft365GraphClientFalso
        {
            MensajeIdsADevolver = ["graph-msg-2"],
            MensajeADevolver = new MensajeGraphDto(
                "graph-msg-2", "graph-thread-1", "Duda sobre CAE", "cliente@ejemplo.com", "<p>otra vez</p>", DateTime.UtcNow, [], []),
        };
        var credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        credencialRepositorio.Agregar(new CredencialIntegracion(conexion.Id, "refresh-token"));
        var servicio = CrearServicio(conexionRepositorio, conversacionRepositorio, graphClient, credencialRepositorio);
        var evento = new EventoWebhook(conexion.Id, "{\"value\":[{}]}");

        await servicio.ProcesarAsync(evento, CancellationToken.None);

        conversacionRepositorio.Conversaciones.Should().ContainSingle();
        conversacionExistente.Mensajes.Should().ContainSingle(m => m.MensajeExternoId == "graph-msg-2");
    }

    [Fact]
    public async Task Ignora_un_mensaje_ya_ingerido_por_idempotencia()
    {
        var conexion = ConexionHabilitada();
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var conversacionExistente = new ConversacionCorreo("Duda sobre CAE");
        conversacionExistente.AsociarConexion(conexion.Id, "graph-thread-1");
        conversacionExistente.AgregarMensaje(DireccionMensaje.Entrante, "cliente@ejemplo.com", "<p>ya estaba</p>", mensajeExternoId: "graph-msg-1");
        var conversacionRepositorio = new ConversacionCorreoRepositorioFalso();
        conversacionRepositorio.Agregar(conversacionExistente);
        var graphClient = new Microsoft365GraphClientFalso
        {
            MensajeIdsADevolver = ["graph-msg-1"], // Graph reenvía la misma notificación
            MensajeADevolver = new MensajeGraphDto(
                "graph-msg-1", "graph-thread-1", "Duda sobre CAE", "cliente@ejemplo.com", "<p>ya estaba</p>", DateTime.UtcNow, [], []),
        };
        var credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        credencialRepositorio.Agregar(new CredencialIntegracion(conexion.Id, "refresh-token"));
        var servicio = CrearServicio(conexionRepositorio, conversacionRepositorio, graphClient, credencialRepositorio);
        var evento = new EventoWebhook(conexion.Id, "{\"value\":[{}]}");

        await servicio.ProcesarAsync(evento, CancellationToken.None);

        evento.Procesado.Should().BeTrue();
        evento.ErrorProcesado.Should().BeNull();
        conversacionExistente.Mensajes.Should().ContainSingle();
    }

    [Fact]
    public async Task No_ingiere_nada_si_la_conexion_esta_deshabilitada()
    {
        var conexion = ConexionHabilitada();
        conexion.Deshabilitar();
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var conversacionRepositorio = new ConversacionCorreoRepositorioFalso();
        var graphClient = new Microsoft365GraphClientFalso { MensajeIdsADevolver = ["graph-msg-1"] };
        var servicio = CrearServicio(conexionRepositorio, conversacionRepositorio, graphClient);
        var evento = new EventoWebhook(conexion.Id, "{\"value\":[{}]}");

        await servicio.ProcesarAsync(evento, CancellationToken.None);

        evento.Procesado.Should().BeTrue();
        conversacionRepositorio.Conversaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Registra_el_fallo_sin_marcar_procesado_si_falla_el_refresco_del_token()
    {
        var conexion = ConexionHabilitada();
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var conversacionRepositorio = new ConversacionCorreoRepositorioFalso();
        var graphClient = new Microsoft365GraphClientFalso { MensajeIdsADevolver = ["graph-msg-1"], FallaRefresco = true };
        var credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        credencialRepositorio.Agregar(new CredencialIntegracion(conexion.Id, "refresh-token"));
        var servicio = CrearServicio(conexionRepositorio, conversacionRepositorio, graphClient, credencialRepositorio);
        var evento = new EventoWebhook(conexion.Id, "{\"value\":[{}]}");

        await servicio.ProcesarAsync(evento, CancellationToken.None);

        evento.Procesado.Should().BeFalse();
        evento.Intentos.Should().Be(1);
        evento.ErrorProcesado.Should().NotBeNullOrEmpty();
    }
}
