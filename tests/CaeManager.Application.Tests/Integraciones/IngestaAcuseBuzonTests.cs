using CaeManager.Application.Integraciones;
using CaeManager.Application.Tests.Comunicaciones;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

/// <summary>Qué correos deja la ingesta como acuse pendiente. El envío en sí está en <see cref="AcuseAutomaticoBuzonServiceTests"/>.</summary>
public class IngestaAcuseBuzonTests
{
    private const string Persona = "persona@ejemplo.test";

    private readonly ConversacionRepositorioFalso _conversaciones = new();
    private readonly ConexionIntegracionRepositorioFalso _conexiones = new();

    private ConexionIntegracion Buzon(bool activo = true, string email = "cae@ejemplo.test")
    {
        var conexion = new ConexionIntegracion(email, "Buzón CAE").Verificada();
        conexion.FijarAcuseAutomatico(activo);
        _conexiones.Agregar(conexion);
        return conexion;
    }

    private const string Autenticado = "mx.ejemplo.test; spf=pass smtp.mailfrom=ejemplo.test";

    private static MensajeGraphDto Mensaje(
        string remitente = Persona, string asunto = "Documentación de julio", string id = "msg-1", string hilo = "hilo-1",
        IReadOnlyList<CabeceraCorreoDto>? cabeceras = null, IReadOnlyList<string>? responderA = null,
        string? autenticacion = Autenticado, DateTime? fechaUtc = null) =>
        new(id, hilo, asunto, remitente, "<p>Texto</p>", fechaUtc ?? DateTime.UtcNow, [], [],
            [.. cabeceras ?? [], .. autenticacion is null ? Array.Empty<CabeceraCorreoDto>() : [new("Authentication-Results", autenticacion)]],
            responderA);

    private async Task<IReadOnlyList<AcusePendiente>> IngerirAsync(
        ConexionIntegracion conexion, MensajeGraphDto mensaje, ResolucionProveedorPlataformaCaeServiceFalso? plataforma = null)
    {
        var graph = new Microsoft365GraphClientFalso { MensajeIdsADevolver = [mensaje.MensajeExternoId], MensajeADevolver = mensaje };
        var credenciales = new CredencialIntegracionRepositorioFalso();
        credenciales.Agregar(new CredencialIntegracion(conexion.Id, "refresh"));
        var ingesta = IngestaWebhookServiceTests.CrearServicio(_conexiones, _conversaciones, graph, credenciales, plataforma);
        var evento = new EventoWebhook(conexion.Id, "{}");

        await ingesta.ProcesarAsync(evento, CancellationToken.None);

        evento.Estado.Should().Be(EstadoEventoWebhook.Completado);
        return ingesta.AcusesPendientes;
    }

    [Fact]
    public async Task El_primer_correo_de_una_conversacion_nueva_deja_un_acuse_al_remitente()
    {
        var conexion = Buzon();

        var acuse = (await IngerirAsync(conexion, Mensaje())).Should().ContainSingle().Subject;

        acuse.Conexion.Should().BeSameAs(conexion);
        acuse.Conversacion.Should().BeSameAs(_conversaciones.Conversaciones.Single());
        acuse.MensajeExternoId.Should().Be("msg-1");
        acuse.Destinatario.Should().Be(Persona);
    }

    [Fact]
    public async Task Un_buzon_con_el_acuse_desactivado_no_deja_ninguno() =>
        (await IngerirAsync(Buzon(activo: false), Mensaje())).Should().BeEmpty();

    [Fact]
    public async Task Un_correo_de_una_conversacion_existente_no_lleva_acuse()
    {
        var conexion = Buzon();
        var existente = new Conversacion("Hilo");
        existente.AsociarConexion(conexion.Id, "hilo-1");
        _conversaciones.Agregar(existente);

        (await IngerirAsync(conexion, Mensaje())).Should().BeEmpty();

        existente.Mensajes.Should().ContainSingle();
    }

    [Fact]
    public async Task La_continuacion_de_un_hilo_cerrado_lleva_numero_nuevo_pero_no_acuse()
    {
        var conexion = Buzon();
        var cerrada = new Conversacion("Hilo");
        cerrada.AsociarConexion(conexion.Id, "hilo-1");
        cerrada.CambiarEstado(EstadoConversacion.Cerrada, DateTime.UtcNow.AddDays(-1));
        _conversaciones.Agregar(cerrada);

        (await IngerirAsync(conexion, Mensaje())).Should().BeEmpty();

        _conversaciones.Conversaciones.Should().Contain(c => c.ConversacionAnteriorId == cerrada.Id);
    }

    [Fact]
    public async Task Reprocesar_el_mismo_correo_no_deja_otro_acuse()
    {
        var conexion = Buzon();

        (await IngerirAsync(conexion, Mensaje())).Should().ContainSingle();
        (await IngerirAsync(conexion, Mensaje())).Should().BeEmpty();
    }

    [Fact]
    public async Task Dos_correos_del_mismo_hilo_en_una_notificacion_dejan_un_acuse()
    {
        var conexion = Buzon();
        var graph = new Microsoft365GraphClientFalso { MensajeIdsADevolver = ["msg-1", "msg-2"] };
        graph.MensajesPorId["msg-1"] = Mensaje();
        graph.MensajesPorId["msg-2"] = Mensaje(id: "msg-2");
        var credenciales = new CredencialIntegracionRepositorioFalso();
        credenciales.Agregar(new CredencialIntegracion(conexion.Id, "refresh"));
        // Como en la base real, el segundo correo no ve la conversación del primero hasta confirmar.
        var conversaciones = new ConversacionRepositorioFalso { NoVeLoSinConfirmar = true };
        var ingesta = IngestaWebhookServiceTests.CrearServicio(_conexiones, conversaciones, graph, credenciales);

        await ingesta.ProcesarAsync(new EventoWebhook(conexion.Id, "{}"), CancellationToken.None);

        conversaciones.Conversaciones.Should().HaveCount(2);
        ingesta.AcusesPendientes.Should().ContainSingle().Which.MensajeExternoId.Should().Be("msg-1");
    }

    [Fact]
    public async Task Un_correo_que_falla_a_medias_no_deja_acuse_y_los_anteriores_del_lote_lo_conservan()
    {
        var conexion = Buzon();
        var graph = new Microsoft365GraphClientFalso { MensajeIdsADevolver = ["msg-1", "msg-2"] };
        graph.MensajesPorId["msg-1"] = Mensaje();
        // Un correo que revienta la ingesta a medias: el evento vuelve a la cola, pero msg-1 ya está ingerido.
        graph.MensajesPorId["msg-2"] = Mensaje(id: "msg-2", hilo: "hilo-2", remitente: null!);
        var credenciales = new CredencialIntegracionRepositorioFalso();
        credenciales.Agregar(new CredencialIntegracion(conexion.Id, "refresh"));
        var ingesta = IngestaWebhookServiceTests.CrearServicio(_conexiones, _conversaciones, graph, credenciales);
        var evento = new EventoWebhook(conexion.Id, "{}");

        await ingesta.ProcesarAsync(evento, CancellationToken.None);

        evento.Estado.Should().NotBe(EstadoEventoWebhook.Completado);
        ingesta.AcusesPendientes.Should().ContainSingle().Which.MensajeExternoId.Should().Be("msg-1");
    }

    [Theory]
    [InlineData("List-Id", "<cae.ejemplo.test>")]
    [InlineData("List-Unsubscribe", "<mailto:baja@ejemplo.test>")]
    [InlineData("List-Post", "<mailto:lista@ejemplo.test>")]
    [InlineData("List-Help", "<mailto:ayuda@ejemplo.test>")]
    public async Task Un_correo_de_lista_que_abre_conversacion_no_lleva_acuse_pero_no_cuenta_como_automatico(string cabecera, string valor)
    {
        var conexion = Buzon();

        (await IngerirAsync(conexion, Mensaje(cabeceras: [new(cabecera, valor)]))).Should().BeEmpty();

        // La respuesta sugerida solo excluye los mensajes marcados como automáticos.
        _conversaciones.Conversaciones.Single().Mensajes.Single().EsAutomatico.Should().BeFalse();
    }

    [Theory]
    [InlineData("List-Id", "<cae.ejemplo.test>")]
    [InlineData("List-Unsubscribe", "<mailto:baja@ejemplo.test>")]
    public async Task Una_respuesta_humana_por_una_lista_reabre_el_hilo(string cabecera, string valor)
    {
        var conexion = Buzon();
        var resuelta = new Conversacion("Hilo");
        resuelta.AsociarConexion(conexion.Id, "hilo-1");
        resuelta.CambiarEstado(EstadoConversacion.Resuelta, DateTime.UtcNow.AddDays(-1));
        _conversaciones.Agregar(resuelta);

        (await IngerirAsync(conexion, Mensaje(cabeceras: [new(cabecera, valor)]))).Should().BeEmpty();

        resuelta.Estado.Should().Be(EstadoConversacion.Abierta);
        resuelta.Mensajes.Single().EsAutomatico.Should().BeFalse();
    }

    [Theory]
    [InlineData("List-Id", "<cae.ejemplo.test>")]
    [InlineData("List-Unsubscribe", "<mailto:baja@ejemplo.test>")]
    public async Task Una_respuesta_humana_por_una_lista_a_un_hilo_cerrado_abre_la_continuacion(string cabecera, string valor)
    {
        var conexion = Buzon();
        var cerrada = new Conversacion("Hilo");
        cerrada.AsociarConexion(conexion.Id, "hilo-1");
        cerrada.CambiarEstado(EstadoConversacion.Cerrada, DateTime.UtcNow.AddDays(-1));
        _conversaciones.Agregar(cerrada);

        (await IngerirAsync(conexion, Mensaje(cabeceras: [new(cabecera, valor)]))).Should().BeEmpty();

        _conversaciones.Conversaciones.Should().ContainSingle(c => c.ConversacionAnteriorId == cerrada.Id);
    }

    public static TheoryData<string, MensajeGraphDto> CorreosSinAcuse => new()
    {
        { "respuesta automática", Mensaje(cabeceras: [new("Auto-Submitted", "auto-replied")]) },
        { "fuera de oficina por cabecera", Mensaje(cabeceras: [new("X-Autoreply", "yes")]) },
        { "fuera de oficina por asunto", Mensaje(asunto: "Automatic reply: Documentación") },
        { "lista por List-Id", Mensaje(cabeceras: [new("List-Id", "<cae.ejemplo.test>")]) },
        { "lista por List-Unsubscribe", Mensaje(cabeceras: [new("List-Unsubscribe", "<mailto:baja@ejemplo.test>")]) },
        { "lista por List-Post", Mensaje(cabeceras: [new("List-Post", "<mailto:lista@ejemplo.test>")]) },
        { "lista por List-Help", Mensaje(cabeceras: [new("List-Help", "<mailto:ayuda@ejemplo.test>")]) },
        { "lista por List-Archive", Mensaje(cabeceras: [new("List-Archive", "<https://ejemplo.test/archivo>")]) },
        { "lista por X-Mailing-List", Mensaje(cabeceras: [new("X-Mailing-List", "lista@ejemplo.test")]) },
        { "lista por Precedence", Mensaje(cabeceras: [new("Precedence", "list")]) },
        { "no-reply", Mensaje(remitente: "no-reply@ejemplo.test") },
        { "rebote", Mensaje(remitente: "mailer-daemon@ejemplo.test") },
        { "rebote sin remite", Mensaje(cabeceras: [new("Return-Path", "<>")]) },
        { "pide silencio", Mensaje(cabeceras: [new("X-Auto-Response-Suppress", "All")]) },
        { "responde a un correo anterior", Mensaje(cabeceras: [new("In-Reply-To", "<anterior@ejemplo.test>")]) },
        { "sin autenticar", Mensaje(autenticacion: null) },
        { "dmarc fallido", Mensaje(autenticacion: "mx.ejemplo.test; dmarc=fail header.from=ejemplo.test") },
        { "spf de otro dominio", Mensaje(autenticacion: "mx.ejemplo.test; spf=pass smtp.mailfrom=otro.test") },
        { "dos resultados de autenticación", Mensaje(cabeceras: [new("Authentication-Results", "spf=pass smtp.mailfrom=ejemplo.test")]) },
        { "demasiado antiguo", Mensaje(fechaUtc: DateTime.UtcNow.AddHours(-2)) },
        { "el propio buzón", Mensaje(remitente: "CAE@ejemplo.test") },
        { "dirección inservible", Mensaje(remitente: "sin arroba") },
        { "nombre con dirección", Mensaje(remitente: "Persona <persona@ejemplo.test>") },
        { "Reply-To a un tercero", Mensaje(responderA: ["tercero@ejemplo.test"]) },
        { "Reply-To múltiple", Mensaje(responderA: [Persona, "tercero@ejemplo.test"]) },
    };

    [Theory]
    [MemberData(nameof(CorreosSinAcuse))]
    public async Task Un_correo_excluido_no_deja_acuse(string motivo, MensajeGraphDto mensaje) =>
        (await IngerirAsync(Buzon(), mensaje)).Should().BeEmpty(motivo);

    [Fact]
    public async Task Un_Reply_To_igual_al_remitente_no_excluye_el_acuse() =>
        (await IngerirAsync(Buzon(), Mensaje(responderA: ["PERSONA@ejemplo.test"]))).Should().ContainSingle();

    [Theory]
    [InlineData("persona@dokify.net", "mx.ejemplo.test; spf=pass smtp.mailfrom=dokify.net")]
    [InlineData("persona@dokify.net", "mx.ejemplo.test; dmarc=pass header.from=dokify.net")]
    [InlineData("noreply@dokify.net", null)]
    public async Task Un_remitente_con_dominio_de_Plataforma_CAE_no_lleva_acuse(string remitente, string? autenticacion)
    {
        var plataforma = new ResolucionProveedorPlataformaCaeServiceFalso();
        plataforma.RegistrarPlataformaPorDominioCorreo(new ProveedorPlataformaCaeCandidatoDto(Guid.NewGuid(), "Dokify", true));

        (await IngerirAsync(Buzon(), Mensaje(remitente: remitente, autenticacion: autenticacion), plataforma)).Should().BeEmpty();

        _conversaciones.Conversaciones.Should().ContainSingle();
    }

    [Theory]
    [InlineData("mx.ejemplo.test; dmarc=pass header.from=ejemplo.test")]
    [InlineData("mx.ejemplo.test; dmarc=none; dkim=pass header.d=ejemplo.test")]
    public async Task Un_remitente_autenticado_con_DMARC_o_DKIM_lleva_acuse(string autenticacion) =>
        (await IngerirAsync(Buzon(), Mensaje(autenticacion: autenticacion))).Should().ContainSingle();

    [Fact]
    public void Un_buzon_personal_no_lleva_acuse_aunque_la_columna_diga_lo_contrario()
    {
        var personal = new ConexionIntegracion("gestora@ejemplo.test", "Personal", gestorPropietarioId: Guid.NewGuid()).Verificada();
        typeof(ConexionIntegracion).GetProperty(nameof(ConexionIntegracion.AcuseAutomaticoActivo))!.SetValue(personal, true);

        ReglaAcuseBuzon.Procede(personal, Mensaje(), dominioDePlataformaCae: false).Should().BeFalse();
    }
}
