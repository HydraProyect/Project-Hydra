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
    [Theory]
    [InlineData(EstadoConversacion.Cerrada, false)]
    [InlineData(EstadoConversacion.Cerrada, true)]
    [InlineData(EstadoConversacion.Resuelta, false)]
    [InlineData(EstadoConversacion.Resuelta, true)]
    [InlineData(EstadoConversacion.EsperandoRespuestaExterna, false)]
    [InlineData(EstadoConversacion.EsperandoRespuestaExterna, true)]
    public async Task El_entrante_continua_un_hilo_cerrado_y_solo_una_respuesta_humana_reabre(
        EstadoConversacion estado, bool automatico)
    {
        var conexion = ConexionHabilitada();
        var conexiones = new ConexionIntegracionRepositorioFalso();
        conexiones.Agregar(conexion);
        var conversaciones = new ConversacionRepositorioFalso();
        var anterior = new Conversacion("Hilo anterior");
        anterior.AsociarConexion(conexion.Id, "hilo");
        anterior.CambiarEstado(estado, DateTime.UtcNow.AddDays(-1));
        conversaciones.Agregar(anterior);
        var graph = new Microsoft365GraphClientFalso
        {
            MensajeIdsADevolver = ["nuevo"],
            MensajeADevolver = new MensajeGraphDto("nuevo", "hilo", "Respuesta", "contacto@ejemplo.test", "Texto",
                DateTime.UtcNow.AddMinutes(-1), [], [], automatico ? [new("Auto-Submitted", "auto-replied")] : [])
        };
        var credenciales = new CredencialIntegracionRepositorioFalso();
        credenciales.Agregar(new CredencialIntegracion(conexion.Id, "refresh"));
        var evento = new EventoWebhook(conexion.Id, "{}");

        await CrearServicio(conexiones, conversaciones, graph, credenciales).ProcesarAsync(evento, CancellationToken.None);

        evento.Estado.Should().Be(EstadoEventoWebhook.Completado);
        if (estado == EstadoConversacion.Cerrada && automatico)
        {
            // El ruido sobre un hilo Cerrado no abre una continuación: se queda en la Cerrada.
            conversaciones.Conversaciones.Should().ContainSingle();
            anterior.Estado.Should().Be(EstadoConversacion.Cerrada);
            anterior.Mensajes.Should().ContainSingle();
        }
        else if (estado == EstadoConversacion.Cerrada)
        {
            anterior.Estado.Should().Be(EstadoConversacion.Cerrada);
            anterior.Mensajes.Should().BeEmpty();
            var nueva = conversaciones.Conversaciones.Should().HaveCount(2).And.Subject.Single(c => c.Id != anterior.Id);
            nueva.ConversacionAnteriorId.Should().Be(anterior.Id);
            nueva.Mensajes.Should().ContainSingle();
        }
        else
        {
            conversaciones.Conversaciones.Should().ContainSingle();
            anterior.Estado.Should().Be(automatico ? estado : EstadoConversacion.Abierta);
            anterior.Mensajes.Should().ContainSingle();
        }
    }

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
            new AlertaCriticaServiceFalso(),
            NullLogger<IngestaWebhookService>.Instance, new CaeManager.Application.Tests.Comunicaciones.IntencionConversacionServiceFalso());

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

    /// <summary>
    /// Sesión nocturna 2026-09-02: hasta que N4 cablea <c>MarcarConError</c>
    /// desde la renovación de suscripción de Graph, este estado era
    /// inalcanzable en producción (solo lo ponía el seeder de demo) y el
    /// mismo descarte silencioso de <see cref="No_ingiere_nada_si_la_conexion_esta_deshabilitada"/>
    /// bastaba. Con esa llamada real, la notificación descartada aquí puede
    /// ser una de verdad — el comportamiento no cambia (sigue sin ingerir
    /// nada ni reintentar), pero queda registrado con un log en vez de
    /// desaparecer sin rastro.
    /// </summary>
    [Fact]
    public async Task No_ingiere_nada_si_la_conexion_esta_en_error()
    {
        var conexion = ConexionHabilitada();
        conexion.MarcarConError("fallo simulado de renovación");
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var conversacionRepositorio = new ConversacionRepositorioFalso();
        var graphClient = new Microsoft365GraphClientFalso { MensajeIdsADevolver = ["graph-msg-1"] };
        var servicio = CrearServicio(conexionRepositorio, conversacionRepositorio, graphClient);
        var evento = new EventoWebhook(conexion.Id, "{\"value\":[{}]}");

        await servicio.ProcesarAsync(evento, CancellationToken.None);

        evento.Estado.Should().Be(EstadoEventoWebhook.Completado);
        evento.ErrorProcesado.Should().BeNull("es un descarte de negocio, no un fallo transitorio que deba reintentarse");
        conversacionRepositorio.Conversaciones.Should().BeEmpty();
    }

    [Theory]
    [InlineData("mx; dmarc=pass header.from=dokify.net", true)]
    [InlineData("mx; dmarc=fail header.from=dokify.net; spf=pass smtp.mailfrom=dokify.net", false)]
    [InlineData(null, false)]
    [InlineData("mx; spf=pass smtp.mailfrom=avisos@dokify.net", true)]
    [InlineData("mx; spf=pass smtp.mailfrom=atacante.test", false)]
    [InlineData("mx; dkim=pass header.d=dokify.net", true)]
    [InlineData("mx; dkim=pass header.d=atacante.test", false)]
    [InlineData("mx; dmarc=pass header.from=atacante.test", false)]
    [InlineData("mx; dmarc=temperror header.from=dokify.net; dkim=pass header.d=dokify.net", false)]
    [InlineData("mx; dmarc=pass header.from=dokify.net; dmarc=fail header.from=dokify.net", false)]
    // Formato real de Microsoft 365, sin authserv-id.
    [InlineData("spf=pass (sender IP is 192.0.2.10) smtp.mailfrom=dokify.net; dkim=pass (signature was verified) header.d=dokify.net;dmarc=pass action=none header.from=dokify.net;compauth=pass reason=100", true)]
    [InlineData("spf=fail (sender IP is 192.0.2.66) smtp.mailfrom=atacante.test; dkim=none (message not signed) header.d=none;dmarc=fail action=none header.from=dokify.net;compauth=fail reason=000", false)]
    // Sin política DMARC publicada: decide SPF alineado o DKIM del propio dominio.
    [InlineData("mx; dmarc=none action=none header.from=dokify.net; dkim=pass header.d=dokify.net", true)]
    [InlineData("mx; dmarc=bestguesspass action=none header.from=dokify.net; spf=pass smtp.mailfrom=dokify.net", true)]
    [InlineData("mx; dmarc=none action=none header.from=dokify.net; spf=pass smtp.mailfrom=atacante.test", false)]
    // Una segunda cabecera puesta por el remitente junto a la del receptor anula la confianza.
    [InlineData("spf=fail smtp.mailfrom=atacante.test;dmarc=fail header.from=dokify.net||mx; dmarc=pass header.from=dokify.net", false)]
    [InlineData("mx; dmarc=pass header.from=dokify.net||spf=fail smtp.mailfrom=atacante.test;dmarc=fail header.from=dokify.net", false)]
    // Mayúsculas, error permanente, valor repetido y subdominio con igualdad estricta.
    [InlineData("mx; DMARC=PASS header.from=DOKIFY.NET", true)]
    [InlineData("mx; dmarc=permerror header.from=dokify.net; spf=pass smtp.mailfrom=dokify.net", false)]
    [InlineData("mx; dmarc=pass header.from=dokify.net header.from=atacante.test", false)]
    [InlineData("mx; dmarc=none header.from=dokify.net; spf=pass smtp.mailfrom=bounce.dokify.net; dkim=pass header.d=sub.dokify.net", false)]
    // Texto del remitente entrecomillado que inyectaría un segmento SPF.
    [InlineData("mx; dmarc=none header.from=dokify.net; spf=softfail smtp.mailfrom=\"x;spf=pass smtp.mailfrom=dokify.net;y=\"@atacante.test", false)]
    // Exchange marca el mensaje como no llegado por Internet: su cabecera de resultados no es de fiar.
    [InlineData("mx; dmarc=pass header.from=dokify.net||X-MS-Exchange-Organization-AuthAs: Internal", false)]
    [InlineData("mx; dmarc=pass header.from=dokify.net||X-MS-Exchange-Organization-AuthAs: Anonymous", true)]
    public async Task Una_plataforma_solo_es_de_confianza_con_autenticacion_alineada(string? autenticacion, bool confiable)
    {
        var conexion = new ConexionIntegracion("persona@empresa.test", "Personal", gestorPropietarioId: Guid.NewGuid());
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var conversacionRepositorio = new ConversacionRepositorioFalso();
        var cerrada = new Conversacion("Hilo cerrado");
        cerrada.AsociarConexion(conexion.Id, "graph-thread-1");
        cerrada.CambiarEstado(EstadoConversacion.Cerrada, DateTime.UtcNow.AddDays(-1));
        conversacionRepositorio.Agregar(cerrada);
        List<CabeceraCorreoDto> cabeceras = [new("Auto-Submitted", "auto-replied")];
        // «||» separa cabeceras; las que empiezan por «X-» llevan su propio nombre antes de «: ».
        foreach (var valor in autenticacion?.Split("||") ?? [])
            cabeceras.Add(valor.StartsWith("X-", StringComparison.Ordinal)
                ? new(valor[..valor.IndexOf(": ", StringComparison.Ordinal)], valor[(valor.IndexOf(": ", StringComparison.Ordinal) + 2)..])
                : new("Authentication-Results", valor));
        var graphClient = new Microsoft365GraphClientFalso
        {
            MensajeIdsADevolver = ["graph-msg-1"],
            MensajeADevolver = new MensajeGraphDto(
                "graph-msg-1", "graph-thread-1", "Aviso pendiente", "noreply@dokify.net", "<p>Aviso</p>", DateTime.UtcNow, [], [], cabeceras),
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
        evento.Estado.Should().Be(EstadoEventoWebhook.Completado);
        clasificacion.EsNotificacionAutomatica.Should().Be(confiable);
        clasificacion.ProveedorPlataformaCaeId.Should().Be(confiable ? proveedorId : null);
        cerrada.Estado.Should().Be(EstadoConversacion.Cerrada);
        if (confiable)
        {
            cerrada.Mensajes.Should().ContainSingle();
            conversacionRepositorio.Conversaciones.Should().ContainSingle();
        }
        else
        {
            clasificacion.Motivo.Should().Be(MotivoRuidoMensaje.PosiblePhishing);
            cerrada.Mensajes.Should().BeEmpty();
            var continuacion = conversacionRepositorio.Conversaciones.Should().HaveCount(2).And.Subject.Single(c => c.Id != cerrada.Id);
            continuacion.ConversacionAnteriorId.Should().Be(cerrada.Id);
            continuacion.Mensajes.Should().ContainSingle();
        }
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
    public async Task Registra_en_ArchivosGuardados_el_adjunto_descargado_para_poder_compensarlo_si_falla_el_guardado()
    {
        var conexion = ConexionHabilitada();
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var conversacionRepositorio = new ConversacionRepositorioFalso();
        var graphClient = new Microsoft365GraphClientFalso
        {
            MensajeIdsADevolver = ["graph-msg-1"],
            MensajeADevolver = new MensajeGraphDto(
                "graph-msg-1", "graph-thread-1", "Duda sobre CAE", "cliente@ejemplo.com", "<p>hola</p>", DateTime.UtcNow,
                [], [new AdjuntoGraphDto("adjunto-1", "informe.pdf", "application/pdf", 1024)]),
            ContenidoAdjuntoADevolver = [1, 2, 3],
        };
        var credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        credencialRepositorio.Agregar(new CredencialIntegracion(conexion.Id, "refresh-token"));
        var servicio = CrearServicio(conexionRepositorio, conversacionRepositorio, graphClient, credencialRepositorio);
        var evento = new EventoWebhook(conexion.Id, "{\"value\":[{}]}");

        await servicio.ProcesarAsync(evento, CancellationToken.None);

        servicio.ArchivosGuardados.Should().ContainSingle().Which.Should().EndWith("informe.pdf");
        var conversacion = conversacionRepositorio.Conversaciones.Should().ContainSingle().Subject;
        var mensaje = conversacion.Mensajes.Should().ContainSingle().Subject;
        mensaje.Adjuntos.Should().ContainSingle(a => a.ArchivoUrl == servicio.ArchivosGuardados[0]);
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
