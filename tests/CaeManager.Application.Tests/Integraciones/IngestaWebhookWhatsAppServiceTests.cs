using CaeManager.Application.Integraciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Comunicaciones;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

public class IngestaWebhookWhatsAppServiceTests
{
    private const string Telefono = "+34600111222";
    private const string PhoneNumberId = "111222333";

    private readonly ConexionIntegracionRepositorioFalso _conexiones = new();
    private readonly LineaWhatsAppRepositorioFalso _lineas = new();
    private readonly ConversacionCorreoRepositorioFalso _conversaciones = new();
    private readonly ContactoWhatsAppRepositorioFalso _contactos = new();
    private readonly ClienteRepositorioFalso _clientes = new();
    private readonly WhatsAppCloudApiClientFalso _whatsApp = new();

    private IngestaWebhookWhatsAppService CrearServicio() =>
        new(_conexiones, _lineas, _conversaciones, _contactos, _clientes, _whatsApp,
            new FileStorageServiceFalso(), NullLogger<IngestaWebhookWhatsAppService>.Instance);

    private ConexionIntegracion CrearLinea(
        ModoAsignacionLinea modo, Guid? comercialId = null, IReadOnlyList<Guid>? pool = null,
        string? mensajeAutoTriage = null, Guid? clienteId = null)
    {
        var conexion = new ConexionIntegracion("+34686543364", "Línea de prueba", clienteId, ProveedorIntegracion.WhatsApp);
        _conexiones.Agregar(conexion);

        var linea = new LineaWhatsApp(conexion.Id, PhoneNumberId, "waba-1", "+34686543364", "token", modo, comercialId, mensajeAutoTriage);
        if (pool is not null) linea.ReemplazarMiembrosPool(pool);
        _lineas.Agregar(linea);

        return conexion;
    }

    private static MensajeEntranteWhatsAppDto MensajeTexto(string wamid, string texto = "Hola") =>
        new(wamid, Telefono, "Chris", "text", texto, null, null, null, DateTime.UtcNow);

    private void ConfigurarNotificacion(
        IReadOnlyList<MensajeEntranteWhatsAppDto>? mensajes = null, IReadOnlyList<EstadoMensajeWhatsAppDto>? estados = null) =>
        _whatsApp.NotificacionADevolver = new NotificacionWhatsAppDto(PhoneNumberId, mensajes ?? [], estados ?? []);

    [Fact]
    public async Task Contacto_conocido_enruta_al_gestor_de_cartera_con_su_cliente()
    {
        var gestorCartera = Guid.NewGuid();
        var cliente = new Cliente("Acme SL", "B12345674", esCritico: false, ejecutivoUsuarioId: gestorCartera);
        _clientes.Clientes.Add(cliente);
        _contactos.Agregar(new ContactoWhatsApp(Telefono, cliente.Id));
        var conexion = CrearLinea(ModoAsignacionLinea.PoolInbound, pool: [Guid.NewGuid()]);
        ConfigurarNotificacion([MensajeTexto("wamid.1")]);

        var avisos = await CrearServicio().ProcesarAsync(new EventoWebhook(conexion.Id, "{}"), CancellationToken.None);

        var conversacion = _conversaciones.Conversaciones.Should().ContainSingle().Subject;
        conversacion.Canal.Should().Be(CanalConversacion.WhatsApp);
        conversacion.ClienteId.Should().Be(cliente.Id);
        conversacion.EjecutivoAsignadoId.Should().Be(gestorCartera);
        _whatsApp.EnviosRealizados.Should().BeEmpty("con cliente identificado no hay auto-mensaje de triage");
        avisos.Should().ContainSingle().Which.ConversacionId.Should().Be(conversacion.Id);
    }

    [Fact]
    public async Task Linea_de_gestor_fijo_asigna_a_su_comercial_y_toma_el_cliente_de_la_conexion()
    {
        var comercial = Guid.NewGuid();
        var clienteId = Guid.NewGuid();
        var conexion = CrearLinea(ModoAsignacionLinea.GestorFijo, comercialId: comercial, clienteId: clienteId);
        ConfigurarNotificacion([MensajeTexto("wamid.1")]);

        await CrearServicio().ProcesarAsync(new EventoWebhook(conexion.Id, "{}"), CancellationToken.None);

        var conversacion = _conversaciones.Conversaciones.Should().ContainSingle().Subject;
        conversacion.EjecutivoAsignadoId.Should().Be(comercial);
        conversacion.ClienteId.Should().Be(clienteId);
    }

    [Fact]
    public async Task El_pool_asigna_al_miembro_con_menos_conversaciones_vivas()
    {
        var ocupado = Guid.NewGuid();
        var libre = Guid.NewGuid();
        var conexion = CrearLinea(ModoAsignacionLinea.PoolInbound, pool: [ocupado, libre]);

        var previa = ConversacionCorreo.CrearWhatsApp("+34999888777", conexion.Id, null, ocupado);
        _conversaciones.Agregar(previa);

        ConfigurarNotificacion([MensajeTexto("wamid.1")]);

        await CrearServicio().ProcesarAsync(new EventoWebhook(conexion.Id, "{}"), CancellationToken.None);

        _conversaciones.Conversaciones.Should().HaveCount(2);
        var nueva = _conversaciones.Conversaciones.Single(c => c.TelefonoContacto == Telefono);
        nueva.EjecutivoAsignadoId.Should().Be(libre);
    }

    [Fact]
    public async Task Sin_cliente_identificado_envia_el_auto_mensaje_de_triage_y_lo_persiste_como_saliente()
    {
        var conexion = CrearLinea(
            ModoAsignacionLinea.PoolInbound, pool: [Guid.NewGuid()], mensajeAutoTriage: "¿De qué empresa nos escribes?");
        ConfigurarNotificacion([MensajeTexto("wamid.1")]);

        await CrearServicio().ProcesarAsync(new EventoWebhook(conexion.Id, "{}"), CancellationToken.None);

        var conversacion = _conversaciones.Conversaciones.Should().ContainSingle().Subject;
        conversacion.ClienteId.Should().BeNull("queda en la cola de triage");
        _whatsApp.EnviosRealizados.Should().ContainSingle().Which.Texto.Should().Be("¿De qué empresa nos escribes?");

        var saliente = conversacion.Mensajes.Single(m => m.Direccion == DireccionMensaje.Saliente);
        saliente.MensajeExternoId.Should().StartWith("wamid.enviado-");
        saliente.EstadoEntrega.Should().Be(EstadoEntregaMensaje.Enviado);
    }

    [Fact]
    public async Task Un_fallo_del_auto_mensaje_no_pierde_el_mensaje_entrante()
    {
        _whatsApp.FallarEnvios = true;
        var conexion = CrearLinea(ModoAsignacionLinea.PoolInbound, pool: [Guid.NewGuid()], mensajeAutoTriage: "¿Empresa?");
        ConfigurarNotificacion([MensajeTexto("wamid.1")]);

        var evento = new EventoWebhook(conexion.Id, "{}");
        await CrearServicio().ProcesarAsync(evento, CancellationToken.None);

        evento.Procesado.Should().BeTrue();
        _conversaciones.Conversaciones.Should().ContainSingle()
            .Which.Mensajes.Should().ContainSingle(m => m.Direccion == DireccionMensaje.Entrante);
    }

    [Fact]
    public async Task Un_wamid_repetido_se_descarta_por_idempotencia()
    {
        var conexion = CrearLinea(ModoAsignacionLinea.GestorFijo, comercialId: Guid.NewGuid());
        ConfigurarNotificacion([MensajeTexto("wamid.1")]);
        var servicio = CrearServicio();

        await servicio.ProcesarAsync(new EventoWebhook(conexion.Id, "{}"), CancellationToken.None);
        var avisosSegundaVez = await servicio.ProcesarAsync(new EventoWebhook(conexion.Id, "{}"), CancellationToken.None);

        _conversaciones.Conversaciones.Should().ContainSingle()
            .Which.Mensajes.Should().ContainSingle();
        avisosSegundaVez.Should().BeEmpty();
    }

    [Fact]
    public async Task Reutiliza_la_conversacion_abierta_del_mismo_telefono_sin_reenrutar()
    {
        var gestorOriginal = Guid.NewGuid();
        var conexion = CrearLinea(ModoAsignacionLinea.GestorFijo, comercialId: Guid.NewGuid());
        var existente = ConversacionCorreo.CrearWhatsApp(Telefono, conexion.Id, null, gestorOriginal);
        existente.AgregarMensaje(DireccionMensaje.Entrante, Telefono, "Primero", mensajeExternoId: "wamid.0");
        _conversaciones.Agregar(existente);
        ConfigurarNotificacion([MensajeTexto("wamid.1", "Segundo")]);

        await CrearServicio().ProcesarAsync(new EventoWebhook(conexion.Id, "{}"), CancellationToken.None);

        _conversaciones.Conversaciones.Should().ContainSingle();
        existente.Mensajes.Should().HaveCount(2);
        existente.EjecutivoAsignadoId.Should().Be(gestorOriginal);
    }

    [Fact]
    public async Task Los_statuses_actualizan_el_estado_de_entrega_del_mensaje_saliente()
    {
        var conexion = CrearLinea(ModoAsignacionLinea.GestorFijo, comercialId: Guid.NewGuid());
        var conversacion = ConversacionCorreo.CrearWhatsApp(Telefono, conexion.Id, null, Guid.NewGuid());
        var saliente = conversacion.AgregarMensaje(DireccionMensaje.Saliente, "+34686543364", "Respuesta", mensajeExternoId: "wamid.out");
        saliente.ActualizarEstadoEntrega(EstadoEntregaMensaje.Enviado);
        _conversaciones.Agregar(conversacion);
        ConfigurarNotificacion(estados:
        [
            new EstadoMensajeWhatsAppDto("wamid.out", "delivered", null),
            new EstadoMensajeWhatsAppDto("wamid.out", "read", null),
            new EstadoMensajeWhatsAppDto("wamid.desconocido", "failed", "no existe"),
        ]);

        var avisos = await CrearServicio().ProcesarAsync(new EventoWebhook(conexion.Id, "{}"), CancellationToken.None);

        saliente.EstadoEntrega.Should().Be(EstadoEntregaMensaje.Leido);
        avisos.Should().BeEmpty("un status no es un mensaje nuevo");
    }

    [Fact]
    public async Task Una_imagen_descarga_el_media_al_almacenamiento()
    {
        _whatsApp.MediaADevolver = new MediaDescargadoWhatsAppDto([1, 2, 3], "image/jpeg");
        var conexion = CrearLinea(ModoAsignacionLinea.GestorFijo, comercialId: Guid.NewGuid());
        ConfigurarNotificacion([
            new MensajeEntranteWhatsAppDto(
                "wamid.img", Telefono, "Chris", "image", null, "media-1", null, "image/jpeg", DateTime.UtcNow),
        ]);

        await CrearServicio().ProcesarAsync(new EventoWebhook(conexion.Id, "{}"), CancellationToken.None);

        var mensaje = _conversaciones.Conversaciones.Single().Mensajes.Single();
        mensaje.CuerpoHtml.Should().Be("[Imagen]");
        mensaje.Adjuntos.Should().ContainSingle().Which.TipoContenido.Should().Be("image/jpeg");
    }

    [Fact]
    public async Task Una_conexion_deshabilitada_descarta_el_evento_sin_reintentos()
    {
        var conexion = CrearLinea(ModoAsignacionLinea.GestorFijo, comercialId: Guid.NewGuid());
        conexion.Deshabilitar();
        ConfigurarNotificacion([MensajeTexto("wamid.1")]);

        var evento = new EventoWebhook(conexion.Id, "{}");
        await CrearServicio().ProcesarAsync(evento, CancellationToken.None);

        evento.Procesado.Should().BeTrue();
        _conversaciones.Conversaciones.Should().BeEmpty();
    }
}
