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
        ConversacionRepositorioFalso conversacionRepositorio,
        Microsoft365GraphClientFalso graphClient,
        CredencialIntegracionRepositorioFalso? credencialRepositorio = null,
        ResolucionProveedorPlataformaCaeServiceFalso? resolucionPlataforma = null,
        ClasificacionRuidoMensajeRepositorioFalso? clasificacionRuidoRepositorio = null) =>
        new(conexionRepositorio, conversacionRepositorio, graphClient,
            new AccesoGraphService(credencialRepositorio ?? new CredencialIntegracionRepositorioFalso(), graphClient),
            new FileStorageServiceFalso(),
            new SugerenciaVisitaCorreoServiceFalso(),
            new SugerenciaGestionCorreoServiceFalso(),
            new ResolucionParticipanteConversacionServiceFalso(),
            resolucionPlataforma ?? new ResolucionProveedorPlataformaCaeServiceFalso(),
            clasificacionRuidoRepositorio ?? new ClasificacionRuidoMensajeRepositorioFalso(),
            new ClasificacionRuidoMensajeServiceFalso(),
            new RelevanciaCaeServiceFalso(),
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
        var conversacionRepositorio = new ConversacionRepositorioFalso();
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

        evento.Estado.Should().Be(EstadoEventoWebhook.Completado);
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
        var conversacionExistente = new Conversacion("Duda sobre CAE");
        conversacionExistente.AsociarConexion(conexion.Id, "graph-thread-1");
        var conversacionRepositorio = new ConversacionRepositorioFalso();
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
        var conversacionExistente = new Conversacion("Duda sobre CAE");
        conversacionExistente.AsociarConexion(conexion.Id, "graph-thread-1");
        conversacionExistente.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@ejemplo.com", "<p>ya estaba</p>", mensajeExternoId: "graph-msg-1");
        var conversacionRepositorio = new ConversacionRepositorioFalso();
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

        evento.Estado.Should().Be(EstadoEventoWebhook.Completado);
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
        var conversacionRepositorio = new ConversacionRepositorioFalso();
        var graphClient = new Microsoft365GraphClientFalso { MensajeIdsADevolver = ["graph-msg-1"] };
        var servicio = CrearServicio(conexionRepositorio, conversacionRepositorio, graphClient);
        var evento = new EventoWebhook(conexion.Id, "{\"value\":[{}]}");

        await servicio.ProcesarAsync(evento, CancellationToken.None);

        evento.Estado.Should().Be(EstadoEventoWebhook.Completado);
        conversacionRepositorio.Conversaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_correo_de_una_plataforma_conocida_queda_marcado_como_notificacion_automatica()
    {
        var conexion = ConexionHabilitada();
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var conversacionRepositorio = new ConversacionRepositorioFalso();
        var graphClient = new Microsoft365GraphClientFalso
        {
            MensajeIdsADevolver = ["graph-msg-1"],
            MensajeADevolver = new MensajeGraphDto(
                "graph-msg-1", "graph-thread-1", "Aviso pendiente", "notificaciones@dokify.net", "<p>Aviso</p>", DateTime.UtcNow, [], []),
        };
        var credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        credencialRepositorio.Agregar(new CredencialIntegracion(conexion.Id, "refresh-token"));
        var proveedorId = Guid.NewGuid();
        var resolucionPlataforma = new ResolucionProveedorPlataformaCaeServiceFalso();
        resolucionPlataforma.RegistrarPlataformaPorDominioCorreo(new ProveedorPlataformaCaeCandidatoDto(proveedorId, "Dokify", true));
        var clasificacionRepositorio = new ClasificacionRuidoMensajeRepositorioFalso();
        var servicio = CrearServicio(
            conexionRepositorio, conversacionRepositorio, graphClient, credencialRepositorio,
            resolucionPlataforma, clasificacionRepositorio);
        var evento = new EventoWebhook(conexion.Id, "{\"value\":[{}]}");

        await servicio.ProcesarAsync(evento, CancellationToken.None);

        var clasificacion = clasificacionRepositorio.Clasificaciones.Should().ContainSingle().Subject;
        clasificacion.EsNotificacionAutomatica.Should().BeTrue();
        clasificacion.ProveedorPlataformaCaeId.Should().Be(proveedorId);
    }

    [Fact]
    public async Task Un_correo_de_remitente_desconocido_no_se_marca_como_notificacion_automatica()
    {
        var conexion = ConexionHabilitada();
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var conversacionRepositorio = new ConversacionRepositorioFalso();
        var graphClient = new Microsoft365GraphClientFalso
        {
            MensajeIdsADevolver = ["graph-msg-1"],
            MensajeADevolver = new MensajeGraphDto(
                "graph-msg-1", "graph-thread-1", "Duda sobre CAE", "cliente@ejemplo.com", "<p>hola</p>", DateTime.UtcNow, [], []),
        };
        var credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        credencialRepositorio.Agregar(new CredencialIntegracion(conexion.Id, "refresh-token"));
        var clasificacionRepositorio = new ClasificacionRuidoMensajeRepositorioFalso();
        var servicio = CrearServicio(
            conexionRepositorio, conversacionRepositorio, graphClient, credencialRepositorio,
            clasificacionRuidoRepositorio: clasificacionRepositorio);
        var evento = new EventoWebhook(conexion.Id, "{\"value\":[{}]}");

        await servicio.ProcesarAsync(evento, CancellationToken.None);

        var clasificacion = clasificacionRepositorio.Clasificaciones.Should().ContainSingle().Subject;
        clasificacion.EsNotificacionAutomatica.Should().BeFalse();
        clasificacion.ProveedorPlataformaCaeId.Should().BeNull();
    }

    [Fact]
    public async Task Registra_el_fallo_sin_marcar_procesado_si_falla_el_refresco_del_token()
    {
        var conexion = ConexionHabilitada();
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var conversacionRepositorio = new ConversacionRepositorioFalso();
        var graphClient = new Microsoft365GraphClientFalso { MensajeIdsADevolver = ["graph-msg-1"], FallaRefresco = true };
        var credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        credencialRepositorio.Agregar(new CredencialIntegracion(conexion.Id, "refresh-token"));
        var servicio = CrearServicio(conexionRepositorio, conversacionRepositorio, graphClient, credencialRepositorio);
        var evento = new EventoWebhook(conexion.Id, "{\"value\":[{}]}");

        await servicio.ProcesarAsync(evento, CancellationToken.None);

        evento.Estado.Should().Be(EstadoEventoWebhook.Pendiente);
        evento.Intentos.Should().Be(1);
        evento.ErrorProcesado.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ClasificarCorreoBuzonPersonal_marca_correo_interno_cuando_comparte_dominio_con_el_buzon()
    {
        IngestaWebhookService.ClasificarCorreoBuzonPersonal("rrhh@arcosspa.com", "gestor@arcosspa.com")
            .Should().Be(MotivoRuidoMensaje.CorreoInterno);
    }

    [Fact]
    public void ClasificarCorreoBuzonPersonal_marca_correo_interno_para_un_subdominio_propio()
    {
        IngestaWebhookService.ClasificarCorreoBuzonPersonal("notificaciones@mail.arcosspa.com", "gestor@arcosspa.com")
            .Should().Be(MotivoRuidoMensaje.CorreoInterno);
    }

    [Fact]
    public void ClasificarCorreoBuzonPersonal_marca_posible_phishing_para_un_dominio_ajeno()
    {
        IngestaWebhookService.ClasificarCorreoBuzonPersonal("alguien@dominio-desconocido.com", "gestor@arcosspa.com")
            .Should().Be(MotivoRuidoMensaje.PosiblePhishing);
    }

    [Fact]
    public void ClasificarCorreoBuzonPersonal_marca_posible_phishing_si_el_remitente_no_tiene_forma_de_email()
    {
        IngestaWebhookService.ClasificarCorreoBuzonPersonal("no-es-un-email", "gestor@arcosspa.com")
            .Should().Be(MotivoRuidoMensaje.PosiblePhishing);
    }
}
