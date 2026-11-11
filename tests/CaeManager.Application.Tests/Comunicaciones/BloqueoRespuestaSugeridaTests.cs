using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Comunicaciones.Commands.EnviarMensajeNuevo;
using CaeManager.Application.Comunicaciones.Commands.MigrarConversacionACorreo;
using CaeManager.Application.Comunicaciones.Commands.ResponderConversacion;
using CaeManager.Application.Comunicaciones.Commands.ResponderConversacionWhatsApp;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaeManager.Application.Tests.Comunicaciones;

/// <summary>
/// Lo que importa de cada comando de envío: con una sugerencia pendiente de revisar no se llama al canal. El doble cuenta
/// las llamadas, porque un fallo sin llamada y una llamada con fallo posterior no se distinguen mirando el resultado.
/// </summary>
public class BloqueoRespuestaSugeridaTests
{
    private const string TextoSugerido = "<p>Estimada Ana:</p><p>Ya hemos recibido su documentación.</p>";
    private const string TextoSugeridoPlano = "Ya hemos recibido su documentación.";
    private const string EscritoAMano = "<p>Te contesto yo.</p>";
    private const string Telefono = "+34600111222";
    private const string Ana = "ana@contrata.test";

    private readonly ComunicacionesQueryContextFalso _comunicaciones = new();
    private readonly ConversacionRepositorioFalso _conversaciones = new();
    private readonly ConexionIntegracionRepositorioFalso _conexiones = new();
    private readonly CredencialIntegracionRepositorioFalso _credenciales = new();
    private readonly IntegracionesQueryContextFalso _integraciones = new();
    private readonly LineaWhatsAppRepositorioFalso _lineas = new();
    private readonly Microsoft365GraphClientFalso _graph = new();
    private readonly WhatsAppCloudApiClientFalso _whatsApp = new();
    private readonly UnitOfWorkFalso _unidad = new();
    private readonly RegistroExportacionFalso _registro = new();

    private RespuestaSugerida Sugerencia(Conversacion conversacion, EstadoRespuestaSugerida estado, string texto = TextoSugerido, Guid? origen = null)
    {
        var respuesta = RespuestaSugerida.DeVega(conversacion.Id, origen ?? Guid.NewGuid(), texto, Guid.NewGuid(), DateTime.UtcNow);
        if (estado is EstadoRespuestaSugerida.SinRevisar or EstadoRespuestaSugerida.Revisada)
            respuesta.Insertar();
        if (estado == EstadoRespuestaSugerida.Revisada)
            respuesta.Confirmar(Guid.NewGuid(), DateTime.UtcNow);
        if (estado == EstadoRespuestaSugerida.Descartada)
            respuesta.Descartar();

        _comunicaciones.ListaRespuestasSugeridas.Add(respuesta);
        return respuesta;
    }

    private ConexionIntegracion Buzon(Guid? clienteId = null)
    {
        var conexion = new ConexionIntegracion("cae@empresa.test", "Buzón CAE", clienteId);
        _conexiones.Agregar(conexion);
        _integraciones.ConexionesLista.Add(conexion);
        _credenciales.Agregar(new CredencialIntegracion(conexion.Id, "refresh-token"));
        return conexion;
    }

    private Conversacion HiloDeCorreo(ConexionIntegracion conexion)
    {
        var conversacion = new Conversacion("Documentación", clienteId: Guid.NewGuid());
        conversacion.AsociarConexion(conexion.Id, "hilo-" + Guid.NewGuid());
        conversacion.AgregarMensaje(
            DireccionMensaje.Entrante, CanalConversacion.Correo, Ana, "<p>Adjunto</p>", mensajeExternoId: "msg-1");
        // Como la ingesta: el remitente queda como participante del hilo, que es lo que mira el bloqueo por buzón.
        _comunicaciones.ListaParticipantesConversacion.Add(
            conversacion.AgregarParticipante(Ana, RolParticipante.De, TipoParticipanteOrigen.Empresa));
        _conversaciones.Agregar(conversacion);
        _comunicaciones.ListaConversaciones.Add(conversacion);
        return conversacion;
    }

    private Task<Result> Responder(Conversacion conversacion, string cuerpo) =>
        new ResponderConversacionCommandHandler(
                _conversaciones, _comunicaciones, _conexiones, new AlcanceDatosServiceFalso(), _graph,
                new AccesoGraphService(_credenciales, _graph), new FileStorageServiceFalso(),
                Options.Create(new ComunicacionesRemitenteOptions()), _unidad, NullLogger<ResponderConversacionCommandHandler>.Instance,
                _registro, new DescarteCambiosPendientesFalso())
            .Handle(new ResponderConversacionCommand(conversacion.Id, cuerpo), CancellationToken.None);

    private Conversacion HiloDeWhatsApp(DateTime ultimoEntranteUtc, out ConexionIntegracion linea)
    {
        linea = new ConexionIntegracion("+34686543364", "Línea", proveedor: ProveedorIntegracion.WhatsApp);
        _conexiones.Agregar(linea);
        _lineas.Agregar(new LineaWhatsApp(
            linea.Id, "111222333", "waba-1", "+34686543364", "token", ModoAsignacionLinea.GestorFijo, Guid.NewGuid()));

        var conversacion = Conversacion.CrearWhatsApp(Telefono, linea.Id, null, Guid.NewGuid());
        conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.WhatsApp, Telefono, "Hola", ultimoEntranteUtc, "wamid.in");
        _conversaciones.Agregar(conversacion);
        _comunicaciones.ListaConversaciones.Add(conversacion);
        return conversacion;
    }

    private Task<Result> ResponderPorWhatsApp(Conversacion conversacion, string texto) =>
        new ResponderConversacionWhatsAppCommandHandler(
                _conversaciones, _comunicaciones, _conexiones, _lineas, new AlcanceDatosServiceFalso(), _whatsApp, _unidad,
                NullLogger<ResponderConversacionWhatsAppCommandHandler>.Instance, _registro, new DescarteCambiosPendientesFalso())
            .Handle(new ResponderConversacionWhatsAppCommand(conversacion.Id, texto), CancellationToken.None);

    private Task<Result> MigrarACorreo(Conversacion conversacion, string cuerpo) =>
        new MigrarConversacionACorreoCommandHandler(
                _conversaciones, _comunicaciones, _integraciones, _conexiones, new AlcanceDatosServiceFalso(), _graph,
                new AccesoGraphService(_credenciales, _graph), new ResolucionParticipanteConversacionServiceFalso(), _unidad, _registro)
            .Handle(new MigrarConversacionACorreoCommand(conversacion.Id, "ana@contrata.test", cuerpo), CancellationToken.None);

    private Task<Result<Guid>> EnviarNuevo(ConexionIntegracion conexion, string cuerpo) =>
        new EnviarMensajeNuevoCommandHandler(
                _conexiones, _conversaciones, _comunicaciones, new AlcanceDatosServiceFalso(), new InvalidadorAlcanceInerte(), _graph,
                new AccesoGraphService(_credenciales, _graph), new FileStorageServiceFalso(),
                new ResolucionParticipanteConversacionServiceFalso(), _unidad, _registro)
            .Handle(new EnviarMensajeNuevoCommand(conexion.Id, ["ana@contrata.test"], "Documentación", cuerpo), CancellationToken.None);

    [Fact]
    public async Task Responder_con_una_sugerencia_sin_revisar_no_llama_al_canal()
    {
        var conversacion = HiloDeCorreo(Buzon());
        Sugerencia(conversacion, EstadoRespuestaSugerida.SinRevisar);

        var resultado = await Responder(conversacion, EscritoAMano);

        resultado.Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        _graph.VecesEnviado.Should().Be(0);
        conversacion.Mensajes.Should().NotContain(m => m.Direccion == DireccionMensaje.Saliente);
        _unidad.VecesGuardado.Should().Be(0);
    }

    /// <summary>El bloqueo va antes que el rastro: quedaría constancia de una salida que no ha ocurrido.</summary>
    [Fact]
    public async Task Un_envio_bloqueado_no_deja_rastro_de_salida()
    {
        var conexion = Buzon();
        var hiloDeCorreo = HiloDeCorreo(conexion);
        Sugerencia(hiloDeCorreo, EstadoRespuestaSugerida.SinRevisar);
        var hiloDeWhatsApp = HiloDeWhatsApp(DateTime.UtcNow.AddHours(-1), out _);
        Sugerencia(hiloDeWhatsApp, EstadoRespuestaSugerida.SinRevisar, TextoSugeridoPlano);
        var hiloAMigrar = HiloDeWhatsApp(DateTime.UtcNow.AddHours(-30), out _);
        Sugerencia(hiloAMigrar, EstadoRespuestaSugerida.SinRevisar);

        (await Responder(hiloDeCorreo, EscritoAMano)).EsFallido.Should().BeTrue();
        (await ResponderPorWhatsApp(hiloDeWhatsApp, "Otro texto")).EsFallido.Should().BeTrue();
        (await MigrarACorreo(hiloAMigrar, EscritoAMano)).EsFallido.Should().BeTrue();
        (await EnviarNuevo(conexion, TextoSugerido)).EsFallido.Should().BeTrue();

        _registro.Filas.Should().BeEmpty();
    }

    [Fact]
    public async Task Responder_tras_confirmar_si_envia()
    {
        var conversacion = HiloDeCorreo(Buzon());
        var respuesta = Sugerencia(conversacion, EstadoRespuestaSugerida.SinRevisar);
        respuesta.Confirmar(Guid.NewGuid(), DateTime.UtcNow);

        var resultado = await Responder(conversacion, TextoSugerido);

        resultado.EsExitoso.Should().BeTrue();
        _graph.VecesEnviado.Should().Be(1);
        respuesta.Estado.Should().Be(EstadoRespuestaSugerida.Revisada);
    }

    [Fact]
    public async Task Responder_escribiendo_a_mano_sin_insertar_si_envia()
    {
        var conversacion = HiloDeCorreo(Buzon());
        Sugerencia(conversacion, EstadoRespuestaSugerida.Propuesta);

        var resultado = await Responder(conversacion, EscritoAMano);

        resultado.EsExitoso.Should().BeTrue();
        _graph.VecesEnviado.Should().Be(1);
    }

    [Theory]
    [InlineData(TextoSugerido)]
    [InlineData("<div>Estimada   Ana:<br>Ya hemos recibido su documentación.</div>")]
    [InlineData("Estimada Ana: Ya hemos recibido su documentación.")]
    [InlineData("<p>Estimada&nbsp;Ana:</p>\r\n<p>Ya hemos recibido su documentaci&oacute;n.</p>")]
    public async Task Responder_con_el_texto_de_una_propuesta_no_llama_al_canal(string cuerpo)
    {
        var conversacion = HiloDeCorreo(Buzon());
        Sugerencia(conversacion, EstadoRespuestaSugerida.Propuesta);

        var resultado = await Responder(conversacion, cuerpo);

        resultado.Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        _graph.VecesEnviado.Should().Be(0);
    }

    [Fact]
    public async Task Responder_con_el_texto_de_una_descartada_si_envia()
    {
        var conversacion = HiloDeCorreo(Buzon());
        Sugerencia(conversacion, EstadoRespuestaSugerida.Descartada);

        var resultado = await Responder(conversacion, TextoSugerido);

        resultado.EsExitoso.Should().BeTrue();
        _graph.VecesEnviado.Should().Be(1);
    }

    [Fact]
    public async Task La_sugerencia_de_otra_conversacion_no_bloquea()
    {
        var conexion = Buzon();
        var conversacion = HiloDeCorreo(conexion);
        Sugerencia(HiloDeCorreo(conexion), EstadoRespuestaSugerida.SinRevisar);

        var resultado = await Responder(conversacion, TextoSugerido);

        resultado.EsExitoso.Should().BeTrue();
        _graph.VecesEnviado.Should().Be(1);
    }

    [Fact]
    public async Task WhatsApp_con_una_sugerencia_sin_revisar_no_llama_al_canal()
    {
        var conversacion = HiloDeWhatsApp(DateTime.UtcNow.AddHours(-1), out _);
        Sugerencia(conversacion, EstadoRespuestaSugerida.SinRevisar, TextoSugeridoPlano);

        var resultado = await ResponderPorWhatsApp(conversacion, "Otro texto");

        resultado.Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        _whatsApp.EnviosRealizados.Should().BeEmpty();
        _unidad.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task WhatsApp_tras_confirmar_si_envia()
    {
        var conversacion = HiloDeWhatsApp(DateTime.UtcNow.AddHours(-1), out _);
        Sugerencia(conversacion, EstadoRespuestaSugerida.Revisada, TextoSugeridoPlano);

        var resultado = await ResponderPorWhatsApp(conversacion, TextoSugeridoPlano);

        resultado.EsExitoso.Should().BeTrue();
        _whatsApp.EnviosRealizados.Should().ContainSingle();
    }

    [Fact]
    public async Task WhatsApp_escribiendo_a_mano_sin_insertar_si_envia()
    {
        var conversacion = HiloDeWhatsApp(DateTime.UtcNow.AddHours(-1), out _);
        Sugerencia(conversacion, EstadoRespuestaSugerida.Propuesta, TextoSugeridoPlano);

        var resultado = await ResponderPorWhatsApp(conversacion, "Otro texto");

        resultado.EsExitoso.Should().BeTrue();
        _whatsApp.EnviosRealizados.Should().ContainSingle();
    }

    [Fact]
    public async Task WhatsApp_con_el_texto_de_una_propuesta_no_llama_al_canal()
    {
        var conversacion = HiloDeWhatsApp(DateTime.UtcNow.AddHours(-1), out _);
        Sugerencia(conversacion, EstadoRespuestaSugerida.Propuesta, TextoSugeridoPlano);

        var resultado = await ResponderPorWhatsApp(conversacion, "  " + TextoSugeridoPlano + " ");

        resultado.Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        _whatsApp.EnviosRealizados.Should().BeEmpty();
    }

    [Fact]
    public async Task Migrar_a_correo_con_una_sugerencia_sin_revisar_no_llama_al_canal()
    {
        Buzon();
        var conversacion = HiloDeWhatsApp(DateTime.UtcNow.AddHours(-30), out _);
        Sugerencia(conversacion, EstadoRespuestaSugerida.SinRevisar);

        var resultado = await MigrarACorreo(conversacion, EscritoAMano);

        resultado.Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        _graph.VecesEnviado.Should().Be(0);
        _unidad.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Migrar_a_correo_tras_confirmar_si_envia()
    {
        Buzon();
        var conversacion = HiloDeWhatsApp(DateTime.UtcNow.AddHours(-30), out _);
        Sugerencia(conversacion, EstadoRespuestaSugerida.Revisada);

        var resultado = await MigrarACorreo(conversacion, TextoSugerido);

        resultado.EsExitoso.Should().BeTrue();
        _graph.VecesEnviado.Should().Be(1);
    }

    [Fact]
    public async Task Migrar_a_correo_escribiendo_a_mano_sin_insertar_si_envia()
    {
        Buzon();
        var conversacion = HiloDeWhatsApp(DateTime.UtcNow.AddHours(-30), out _);
        Sugerencia(conversacion, EstadoRespuestaSugerida.Propuesta);

        var resultado = await MigrarACorreo(conversacion, EscritoAMano);

        resultado.EsExitoso.Should().BeTrue();
        _graph.VecesEnviado.Should().Be(1);
    }

    [Fact]
    public async Task Migrar_a_correo_con_el_texto_de_una_propuesta_no_llama_al_canal()
    {
        Buzon();
        var conversacion = HiloDeWhatsApp(DateTime.UtcNow.AddHours(-30), out _);
        Sugerencia(conversacion, EstadoRespuestaSugerida.Propuesta);

        var resultado = await MigrarACorreo(conversacion, TextoSugerido);

        resultado.Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        _graph.VecesEnviado.Should().Be(0);
    }

    [Theory]
    [InlineData(EstadoRespuestaSugerida.Propuesta)]
    [InlineData(EstadoRespuestaSugerida.SinRevisar)]
    public async Task Mensaje_nuevo_desde_el_buzon_con_el_texto_de_una_sugerencia_activa_no_llama_al_canal(EstadoRespuestaSugerida estado)
    {
        var conexion = Buzon();
        Sugerencia(HiloDeCorreo(conexion), estado);

        var resultado = await EnviarNuevo(conexion, TextoSugerido);

        resultado.Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        _graph.VecesEnviado.Should().Be(0);
    }

    [Fact]
    public async Task Mensaje_nuevo_tras_confirmar_si_envia()
    {
        var conexion = Buzon();
        Sugerencia(HiloDeCorreo(conexion), EstadoRespuestaSugerida.Revisada);

        var resultado = await EnviarNuevo(conexion, TextoSugerido);

        resultado.EsExitoso.Should().BeTrue();
        _graph.VecesEnviado.Should().Be(1);
    }

    [Fact]
    public async Task Mensaje_nuevo_escrito_a_mano_si_envia()
    {
        var conexion = Buzon();
        Sugerencia(HiloDeCorreo(conexion), EstadoRespuestaSugerida.SinRevisar);

        var resultado = await EnviarNuevo(conexion, EscritoAMano);

        resultado.EsExitoso.Should().BeTrue();
        _graph.VecesEnviado.Should().Be(1);
    }

    [Fact]
    public async Task Mensaje_nuevo_desde_otro_buzon_no_mira_las_sugerencias_de_este()
    {
        Sugerencia(HiloDeCorreo(Buzon()), EstadoRespuestaSugerida.SinRevisar);

        var resultado = await EnviarNuevo(Buzon(), TextoSugerido);

        resultado.EsExitoso.Should().BeTrue();
        _graph.VecesEnviado.Should().Be(1);
    }

    private Guid EntranteDePersona(Conversacion conversacion, bool esAutomatico = false)
    {
        var mensaje = conversacion.AgregarMensaje(
            DireccionMensaje.Entrante, CanalConversacion.Correo, "ana@contrata.test", "<p>Otra cosa</p>", DateTime.UtcNow, esAutomatico: esAutomatico);
        _comunicaciones.ListaMensajes.Add(mensaje);
        return mensaje.Id;
    }

    [Fact]
    public async Task El_texto_descartado_para_el_ultimo_entrante_no_sale()
    {
        var conversacion = HiloDeCorreo(Buzon());
        Sugerencia(conversacion, EstadoRespuestaSugerida.Descartada, origen: EntranteDePersona(conversacion));

        var resultado = await Responder(conversacion, TextoSugerido);

        resultado.Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        _graph.VecesEnviado.Should().Be(0);
    }

    [Fact]
    public async Task El_ultimo_entrante_no_cuenta_los_automaticos()
    {
        var conversacion = HiloDeCorreo(Buzon());
        var dePersona = EntranteDePersona(conversacion);
        EntranteDePersona(conversacion, esAutomatico: true);
        Sugerencia(conversacion, EstadoRespuestaSugerida.Descartada, origen: dePersona);

        (await Responder(conversacion, TextoSugerido)).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
    }

    [Fact]
    public async Task El_texto_descartado_para_un_entrante_anterior_si_sale()
    {
        var conversacion = HiloDeCorreo(Buzon());
        var anterior = EntranteDePersona(conversacion);
        EntranteDePersona(conversacion);
        Sugerencia(conversacion, EstadoRespuestaSugerida.Descartada, origen: anterior);

        (await Responder(conversacion, TextoSugerido)).EsExitoso.Should().BeTrue();
        _graph.VecesEnviado.Should().Be(1);
    }

    [Theory]
    [InlineData(EstadoRespuestaSugerida.Descartada)]
    [InlineData(EstadoRespuestaSugerida.Propuesta)]
    public async Task Si_una_persona_confirmo_ese_texto_para_ese_mensaje_si_sale(EstadoRespuestaSugerida estado)
    {
        var conversacion = HiloDeCorreo(Buzon());
        var entrante = EntranteDePersona(conversacion);
        Sugerencia(conversacion, estado, origen: entrante);
        Sugerencia(conversacion, EstadoRespuestaSugerida.Revisada, origen: entrante);

        (await Responder(conversacion, TextoSugerido)).EsExitoso.Should().BeTrue();
        _graph.VecesEnviado.Should().Be(1);
    }

    [Fact]
    public async Task Confirmar_ese_texto_para_un_mensaje_anterior_no_revisa_la_propuesta_del_ultimo()
    {
        var conversacion = HiloDeCorreo(Buzon());
        var anterior = EntranteDePersona(conversacion);
        var ultimo = EntranteDePersona(conversacion);
        Sugerencia(conversacion, EstadoRespuestaSugerida.Revisada, origen: anterior);
        Sugerencia(conversacion, EstadoRespuestaSugerida.Propuesta, origen: ultimo);

        (await Responder(conversacion, TextoSugerido)).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
    }

    [Fact]
    public async Task La_coincidencia_en_un_hilo_que_quien_envia_no_ve_no_bloquea_ni_delata()
    {
        var conexion = Buzon();
        Sugerencia(HiloDeCorreo(conexion), EstadoRespuestaSugerida.Propuesta);

        var sinVer = await _comunicaciones.ComprobarEnvioPorBuzonAsync(
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false), new InvalidadorAlcanceInerte(), conexion.Id, [Ana], TextoSugerido, CancellationToken.None);
        var viendolo = await _comunicaciones.ComprobarEnvioPorBuzonAsync(
            new AlcanceDatosServiceFalso(), new InvalidadorAlcanceInerte(), conexion.Id, [Ana], TextoSugerido, CancellationToken.None);

        sinVer.EsExitoso.Should().BeTrue();
        viendolo.Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
    }

    [Theory]
    [InlineData(TextoSugerido + "<p>Un saludo, Marta</p>")]
    [InlineData("<p>Buenos días.</p>" + TextoSugerido)]
    [InlineData("Hola. Estimada Ana: Ya hemos recibido su documentación. Un saludo.")]
    public async Task Anadir_firma_o_saludo_a_una_propuesta_no_elude_el_bloqueo(string cuerpo)
    {
        var conversacion = HiloDeCorreo(Buzon());
        Sugerencia(conversacion, EstadoRespuestaSugerida.Propuesta);

        (await Responder(conversacion, cuerpo)).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        _graph.VecesEnviado.Should().Be(0);
    }

    [Fact]
    public async Task Anadir_firma_a_la_descartada_del_ultimo_entrante_no_elude_el_bloqueo()
    {
        var conversacion = HiloDeCorreo(Buzon());
        Sugerencia(conversacion, EstadoRespuestaSugerida.Descartada, origen: EntranteDePersona(conversacion));

        (await Responder(conversacion, TextoSugerido + "<p>Un saludo</p>")).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
    }

    [Fact]
    public async Task Anadir_firma_a_la_descartada_de_un_entrante_anterior_si_sale()
    {
        var conversacion = HiloDeCorreo(Buzon());
        var anterior = EntranteDePersona(conversacion);
        EntranteDePersona(conversacion);
        Sugerencia(conversacion, EstadoRespuestaSugerida.Descartada, origen: anterior);

        (await Responder(conversacion, TextoSugerido + "<p>Un saludo</p>")).EsExitoso.Should().BeTrue();
    }

    [Theory]
    [InlineData(EstadoRespuestaSugerida.Propuesta)]
    [InlineData(EstadoRespuestaSugerida.Descartada)]
    public async Task Una_edicion_por_dentro_de_mas_del_umbral_si_sale(EstadoRespuestaSugerida estado)
    {
        var conversacion = HiloDeCorreo(Buzon());
        Sugerencia(conversacion, estado, origen: EntranteDePersona(conversacion));

        var resultado = await Responder(conversacion, EditadaMasDelUmbral);

        resultado.EsExitoso.Should().BeTrue();
        _graph.VecesEnviado.Should().Be(1);
    }

    /// <summary>26 caracteres cambiados por dentro: más que <see cref="RevisionRespuestaSugerida.UmbralEdicion"/>.</summary>
    private const string EditadaMasDelUmbral =
        "<p>Estimada Ana:</p><p>Ya hemos recibido toda su documentación, que revisaremos hoy.</p>";

    /// <summary>
    /// Revisión Codex r1 (#2163, ALTO 1): copiar a mano la propuesta que enseña el panel, sin «Insertar», y cambiarle una
    /// letra por dentro no la revisa. Antes solo bloqueaban la igualdad y la contención, y esto salía por el canal.
    /// </summary>
    [Theory]
    [InlineData(EstadoRespuestaSugerida.Propuesta, "<p>Estimada Ana:</p><p>Ya hemos recibido su documentacion.</p>")]
    [InlineData(EstadoRespuestaSugerida.Propuesta, "Estimada Ana: Ya hemos recibdo su documentación.")]
    [InlineData(EstadoRespuestaSugerida.Descartada, "<p>Estimada Ana:</p><p>Ya hemos recibido la documentación.</p>")]
    public async Task Copiar_la_propuesta_y_cambiar_una_letra_no_llama_al_canal(EstadoRespuestaSugerida estado, string cuerpo)
    {
        var conversacion = HiloDeCorreo(Buzon());
        Sugerencia(conversacion, estado, origen: EntranteDePersona(conversacion));

        var resultado = await Responder(conversacion, cuerpo);

        resultado.Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        _graph.VecesEnviado.Should().Be(0);
    }

    [Fact]
    public async Task Copiar_la_propuesta_y_cambiar_una_letra_no_sale_por_WhatsApp_ni_por_correo_de_continuacion()
    {
        var whatsApp = HiloDeWhatsApp(DateTime.UtcNow.AddHours(-1), out _);
        Sugerencia(whatsApp, EstadoRespuestaSugerida.Propuesta, "Buenos días, Ana. Ya hemos recibido su documentación.");
        (await ResponderPorWhatsApp(whatsApp, "Buenos dias, Ana. Ya hemos recibido su documentación."))
            .Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        _whatsApp.EnviosRealizados.Should().BeEmpty();

        Buzon();
        var fueraDeVentana = HiloDeWhatsApp(DateTime.UtcNow.AddHours(-30), out _);
        Sugerencia(fueraDeVentana, EstadoRespuestaSugerida.Propuesta);
        (await MigrarACorreo(fueraDeVentana, "<p>Estimada Ana:</p><p>Ya hemos recibido su documentacion.</p>"))
            .Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        _graph.VecesEnviado.Should().Be(0);
    }

    /// <summary>
    /// Revisión Codex r1 (#2163, MEDIO 8): el redactor de correo recibe la sugerencia como texto, no como HTML. Lo que
    /// pinta el redactor (<see cref="VariablesPlantillaRespuesta.HtmlATexto"/> del HTML generado) se compara como el HTML
    /// guardado: el mismo hash, y bloquea.
    /// </summary>
    [Fact]
    public async Task La_sugerencia_de_correo_pasada_a_texto_para_el_redactor_sigue_bloqueando()
    {
        var conversacion = HiloDeCorreo(Buzon());
        var html = VariablesPlantillaRespuesta.TextoPlanoAHtml(
            "Estimada Ana:\n\nYa hemos recibido su documentación & la revisamos hoy (5<10).\nUn saludo.");
        Sugerencia(conversacion, EstadoRespuestaSugerida.Propuesta, html);
        var enElRedactor = VariablesPlantillaRespuesta.HtmlATexto(html);

        enElRedactor.Should().NotContain("<p>").And.NotContain("&amp;");
        RespuestaSugerida.CalcularTextoHash(enElRedactor).Should().Be(RespuestaSugerida.CalcularTextoHash(html));
        RevisionRespuestaSugerida.EditadaLoSuficiente(enElRedactor, html).Should().BeFalse("se mide el texto, no el marcado");
        (await Responder(conversacion, enElRedactor)).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
    }

    [Theory]
    [InlineData("<p>Estimada Ana:</p><p>Ya hemos recibido su documentación.</p>", 0)]
    [InlineData("<div><b>Estimada</b> Ana:<br>Ya hemos recibido su documentación.</div>", 0)]
    [InlineData("<p>Estimada Ana:</p><p>Ya hemos recibido su documentacion.</p>", 1)]
    public void La_distancia_se_mide_sobre_el_texto_y_no_sobre_las_etiquetas(string cuerpo, int distancia) =>
        RevisionRespuestaSugerida.DistanciaEdicion(
                RevisionRespuestaSugerida.TextoComparable(cuerpo), RevisionRespuestaSugerida.TextoComparable(TextoSugerido))
            .Should().Be(distancia);

    /// <summary>
    /// Revisión Codex r2 (#2163, ALTO): el servidor aplica la regla de la interfaz, sin excepción por longitud. Una sugerencia
    /// corta sigue sin revisar con 15 caracteres cambiados o menos, y deja de estarlo con 16, igual que en una larga; y para
    /// los mismos textos la interfaz (<see cref="RevisionRespuestaSugerida.EditadaLoSuficiente"/>) dice exactamente lo contrario.
    /// </summary>
    [Theory]
    [InlineData("Ok, gracias. Marta", "Ok, gracias", true)]
    [InlineData("Ok, grazias", "Ok, gracias", true)]
    [InlineData("Gracias", "Gracias, lo reviso.", true)]
    [InlineData("Vale", "Ok", true)]
    [InlineData("", "", true)]
    [InlineData("Ok, gracias.12345678901234", "Ok, gracias", true)]
    [InlineData("Ok, gracias.123456789012345", "Ok, gracias", false)]
    public void En_una_sugerencia_corta_el_umbral_es_el_mismo_que_en_la_interfaz(string cuerpo, string sugerencia, bool casiLaMisma)
    {
        RevisionRespuestaSugerida.EsCasiLaMisma(cuerpo, sugerencia).Should().Be(casiLaMisma);
        RevisionRespuestaSugerida.EditadaLoSuficiente(cuerpo, sugerencia).Should().Be(!casiLaMisma);
    }

    [Fact]
    public async Task Si_una_persona_confirmo_ese_texto_para_ese_mensaje_tambien_sale_con_firma()
    {
        var conversacion = HiloDeCorreo(Buzon());
        var entrante = EntranteDePersona(conversacion);
        Sugerencia(conversacion, EstadoRespuestaSugerida.Descartada, origen: entrante);
        Sugerencia(conversacion, EstadoRespuestaSugerida.Revisada, origen: entrante);

        (await Responder(conversacion, TextoSugerido + "<p>Un saludo</p>")).EsExitoso.Should().BeTrue();
    }

    /// <summary>
    /// Por debajo de <see cref="BloqueoRespuestaSugerida.LongitudMinimaTextoContenido"/> no se bloquea por contención: un
    /// texto corto dentro de un mensaje que lo aparta más del umbral sale. Con 15 caracteres añadidos o menos sigue siendo
    /// casi el mismo y bloquea, tenga la longitud que tenga (revisión Codex r2, ALTO).
    /// </summary>
    [Theory]
    [InlineData(BloqueoRespuestaSugerida.LongitudMinimaTextoContenido - 1, true)]
    [InlineData(BloqueoRespuestaSugerida.LongitudMinimaTextoContenido, false)]
    public async Task Un_texto_corto_no_bloquea_por_contencion(int longitud, bool sale)
    {
        var conversacion = HiloDeCorreo(Buzon());
        var texto = new string('a', longitud);
        Sugerencia(conversacion, EstadoRespuestaSugerida.Propuesta, texto);

        (await Responder(conversacion, texto + FirmaLarga)).EsExitoso.Should().Be(sale);
        (await Responder(conversacion, texto + " Un saludo")).EsExitoso.Should().BeFalse("diez caracteres añadidos no son revisión");
        (await Responder(conversacion, texto)).EsExitoso.Should().BeFalse();
    }

    /// <summary>Más que <see cref="RevisionRespuestaSugerida.UmbralEdicion"/> caracteres: no es «casi la misma».</summary>
    private const string FirmaLarga = " Un saludo cordial, Marta Gil";

    [Fact]
    public async Task Una_firma_corta_sobre_un_acuse_breve_no_elude_el_bloqueo()
    {
        var conversacion = HiloDeWhatsApp(DateTime.UtcNow.AddHours(-1), out _);
        Sugerencia(conversacion, EstadoRespuestaSugerida.Propuesta, "Recibido, muchas gracias.");

        (await ResponderPorWhatsApp(conversacion, "Recibido, muchas gracias. Marta")).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        _whatsApp.EnviosRealizados.Should().BeEmpty();
    }

    /// <summary>
    /// Revisión Codex r2 (#2163, ALTO): «Ok, gracias» con siete caracteres añadidos sigue sin revisar y no sale por ningún
    /// canal; con dieciséis, sí. Antes el margen de una sugerencia corta bajaba a la mitad de su longitud (5) y esto salía.
    /// </summary>
    [Fact]
    public async Task Un_acuse_breve_con_firma_corta_no_sale_por_ningun_canal_y_con_mas_del_umbral_si()
    {
        const string acuse = "Ok, gracias";
        var whatsApp = HiloDeWhatsApp(DateTime.UtcNow.AddHours(-1), out _);
        Sugerencia(whatsApp, EstadoRespuestaSugerida.Propuesta, acuse);
        (await ResponderPorWhatsApp(whatsApp, "Ok, gracias. Marta")).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        (await ResponderPorWhatsApp(whatsApp, acuse + new string('.', RevisionRespuestaSugerida.UmbralEdicion)))
            .Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        _whatsApp.EnviosRealizados.Should().BeEmpty();
        (await ResponderPorWhatsApp(whatsApp, acuse + new string('.', RevisionRespuestaSugerida.UmbralEdicion + 1))).EsExitoso.Should().BeTrue();

        var conexion = Buzon();
        var correo = HiloDeCorreo(conexion);
        Sugerencia(correo, EstadoRespuestaSugerida.Propuesta, "<p>Ok, gracias</p>");
        (await Responder(correo, "Ok, gracias. Marta")).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        (await EnviarNuevo(conexion, "Ok, gracias. Marta")).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        _graph.VecesEnviado.Should().Be(0);

        var fueraDeVentana = HiloDeWhatsApp(DateTime.UtcNow.AddHours(-30), out _);
        Sugerencia(fueraDeVentana, EstadoRespuestaSugerida.Propuesta, acuse);
        (await MigrarACorreo(fueraDeVentana, "Ok, gracias. Marta")).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        _graph.VecesEnviado.Should().Be(0);
    }

    [Fact]
    public async Task Marcar_una_negrita_dentro_de_una_palabra_no_elude_el_bloqueo()
    {
        var conversacion = HiloDeCorreo(Buzon());
        Sugerencia(conversacion, EstadoRespuestaSugerida.Propuesta);

        var resultado = await Responder(conversacion, "<p>Estimada Ana:</p><p>Ya hemos reci<b>bi</b>do su documentación.</p>");

        resultado.Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        _graph.VecesEnviado.Should().Be(0);
    }

    [Fact]
    public async Task Mensaje_nuevo_con_el_texto_de_la_descartada_del_ultimo_entrante_no_llama_al_canal()
    {
        var conexion = Buzon();
        var conversacion = HiloDeCorreo(conexion);
        Sugerencia(conversacion, EstadoRespuestaSugerida.Descartada, origen: EntranteDePersona(conversacion));

        var resultado = await EnviarNuevo(conexion, TextoSugerido);

        resultado.Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        _graph.VecesEnviado.Should().Be(0);
    }

    [Fact]
    public async Task Mensaje_nuevo_con_el_texto_descartado_para_un_entrante_anterior_si_sale()
    {
        var conexion = Buzon();
        var conversacion = HiloDeCorreo(conexion);
        var anterior = EntranteDePersona(conversacion);
        EntranteDePersona(conversacion);
        Sugerencia(conversacion, EstadoRespuestaSugerida.Descartada, origen: anterior);

        (await EnviarNuevo(conexion, TextoSugerido)).EsExitoso.Should().BeTrue();
        _graph.VecesEnviado.Should().Be(1);
    }

    [Theory]
    [InlineData(EstadoRespuestaSugerida.Descartada)]
    [InlineData(EstadoRespuestaSugerida.Propuesta)]
    public async Task Mensaje_nuevo_si_una_persona_confirmo_ese_texto_para_ese_mensaje_si_sale(EstadoRespuestaSugerida estado)
    {
        var conexion = Buzon();
        var conversacion = HiloDeCorreo(conexion);
        var entrante = EntranteDePersona(conversacion);
        Sugerencia(conversacion, estado, origen: entrante);
        Sugerencia(conversacion, EstadoRespuestaSugerida.Revisada, origen: entrante);

        (await EnviarNuevo(conexion, TextoSugerido)).EsExitoso.Should().BeTrue();
        _graph.VecesEnviado.Should().Be(1);
    }

    [Fact]
    public async Task La_descartada_de_un_hilo_que_quien_envia_no_ve_no_bloquea_ni_delata()
    {
        var conexion = Buzon();
        var conversacion = HiloDeCorreo(conexion);
        Sugerencia(conversacion, EstadoRespuestaSugerida.Descartada, origen: EntranteDePersona(conversacion));

        var sinVer = await _comunicaciones.ComprobarEnvioPorBuzonAsync(
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false), new InvalidadorAlcanceInerte(), conexion.Id, [Ana], TextoSugerido, CancellationToken.None);

        sinVer.EsExitoso.Should().BeTrue();
    }

    private const string CuerpoSinRelacion = "<p>Cuerpo sin relación alguna con ninguna sugerencia.</p>";

    private Task<Result> PorBuzon(
        ConexionIntegracion conexion, string cuerpo, IAlcanceDatosService? alcance = null, IInvalidadorAlcance? invalidador = null,
        string destinatario = Ana) =>
        _comunicaciones.ComprobarEnvioPorBuzonAsync(
            alcance ?? new AlcanceDatosServiceFalso(), invalidador ?? new InvalidadorAlcanceInerte(), conexion.Id, [destinatario], cuerpo,
            CancellationToken.None);

    [Theory]
    [InlineData(EstadoRespuestaSugerida.Propuesta)]
    [InlineData(EstadoRespuestaSugerida.SinRevisar)]
    public async Task Por_buzon_copiar_la_sugerencia_y_anadir_una_firma_no_elude_el_bloqueo(EstadoRespuestaSugerida estado)
    {
        var conexion = Buzon();
        Sugerencia(HiloDeCorreo(conexion), estado);

        (await EnviarNuevo(conexion, TextoSugerido + "<p>Un saludo, Marta</p>")).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        _graph.VecesEnviado.Should().Be(0);
        (await PorBuzon(conexion, "<p>Buenos días.</p>" + TextoSugerido)).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        (await PorBuzon(conexion, "Hola. Estimada Ana: Ya hemos recibido su documentación. Un saludo."))
            .Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
    }

    [Fact]
    public async Task Por_buzon_la_misma_sugerencia_revisada_no_bloquea_ni_con_firma()
    {
        var conexion = Buzon();
        var conversacion = HiloDeCorreo(conexion);
        var entrante = EntranteDePersona(conversacion);
        Sugerencia(conversacion, EstadoRespuestaSugerida.Revisada, origen: entrante);

        (await PorBuzon(conexion, TextoSugerido)).EsExitoso.Should().BeTrue();
        (await PorBuzon(conexion, TextoSugerido + "<p>Un saludo, Marta</p>")).EsExitoso.Should().BeTrue();
    }

    [Theory]
    [InlineData(EstadoRespuestaSugerida.Propuesta)]
    [InlineData(EstadoRespuestaSugerida.Descartada)]
    public async Task Por_buzon_confirmar_ese_texto_para_ese_mensaje_desbloquea_tambien_con_firma(EstadoRespuestaSugerida estado)
    {
        var conexion = Buzon();
        var conversacion = HiloDeCorreo(conexion);
        var entrante = EntranteDePersona(conversacion);
        Sugerencia(conversacion, estado, origen: entrante);
        Sugerencia(conversacion, EstadoRespuestaSugerida.Revisada, origen: entrante);

        (await PorBuzon(conexion, TextoSugerido + "<p>Un saludo</p>")).EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task Por_buzon_la_descartada_del_ultimo_entrante_con_firma_bloquea_y_la_de_uno_anterior_no()
    {
        var conexion = Buzon();
        var conversacion = HiloDeCorreo(conexion);
        var anterior = EntranteDePersona(conversacion);
        Sugerencia(conversacion, EstadoRespuestaSugerida.Descartada, origen: anterior);
        var ultimo = EntranteDePersona(conversacion);

        (await PorBuzon(conexion, TextoSugerido + "<p>Un saludo</p>")).EsExitoso.Should().BeTrue();

        Sugerencia(conversacion, EstadoRespuestaSugerida.Descartada, origen: ultimo);

        (await PorBuzon(conexion, TextoSugerido + "<p>Un saludo</p>")).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
    }

    [Fact]
    public async Task Por_buzon_el_texto_contenido_de_un_hilo_que_quien_envia_no_ve_no_bloquea_ni_delata()
    {
        var conexion = Buzon();
        var ajeno = HiloDeCorreo(conexion);
        Sugerencia(ajeno, EstadoRespuestaSugerida.Propuesta);
        var cuerpo = TextoSugerido + "<p>Un saludo</p>";
        var sinVer = new AlcanceDatosServiceFalso(tieneAccesoTotal: false);

        (await PorBuzon(conexion, cuerpo, sinVer)).EsExitoso.Should().BeTrue();
        (await PorBuzon(conexion, cuerpo)).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
    }

    /// <summary>La visibilidad se decide hilo a hilo: lo que hay en un hilo ajeno ni bloquea ni desbloquea el visible.</summary>
    [Fact]
    public async Task Por_buzon_un_hilo_ajeno_no_desbloquea_ni_bloquea_al_visible()
    {
        var conexion = Buzon();
        var visible = HiloDeCorreo(conexion);
        var ajeno = HiloDeCorreo(conexion);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [visible.ClienteId!.Value]);
        var cuerpo = TextoSugerido + "<p>Un saludo</p>";
        var origen = Guid.NewGuid();

        Sugerencia(ajeno, EstadoRespuestaSugerida.Propuesta, origen: origen);
        (await PorBuzon(conexion, cuerpo, alcance)).EsExitoso.Should().BeTrue();

        Sugerencia(visible, EstadoRespuestaSugerida.Propuesta, origen: origen);
        Sugerencia(ajeno, EstadoRespuestaSugerida.Revisada, origen: origen);
        (await PorBuzon(conexion, cuerpo, alcance)).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
    }

    [Theory]
    [InlineData(BloqueoRespuestaSugerida.LongitudMinimaTextoContenido - 1, true)]
    [InlineData(BloqueoRespuestaSugerida.LongitudMinimaTextoContenido, false)]
    public async Task Por_buzon_un_texto_corto_no_bloquea_por_contencion(int longitud, bool sale)
    {
        var conexion = Buzon();
        var texto = new string('a', longitud);
        Sugerencia(HiloDeCorreo(conexion), EstadoRespuestaSugerida.Propuesta, texto);

        (await PorBuzon(conexion, texto + FirmaLarga)).EsExitoso.Should().Be(sale);
        (await PorBuzon(conexion, texto + " Un saludo")).EsExitoso.Should().BeFalse("diez caracteres añadidos no son revisión");
        (await PorBuzon(conexion, texto)).EsExitoso.Should().BeFalse();
    }

    /// <summary>
    /// Revisión Codex r2 (#2163, MEDIO 4): por buzón solo cuentan los hilos con el destinatario del mensaje. Una sugerencia
    /// pendiente de la conversación con Ana, a seis caracteres del cuerpo, no bloquea un mensaje independiente a otra
    /// contraparte; el mismo cuerpo a Ana sí bloquea (también con la dirección en mayúsculas o en copia).
    /// </summary>
    [Fact]
    public async Task Por_buzon_la_sugerencia_de_la_conversacion_con_otra_contraparte_no_bloquea_y_la_del_destinatario_si()
    {
        var conexion = Buzon();
        Sugerencia(HiloDeCorreo(conexion), EstadoRespuestaSugerida.Propuesta);
        const string parecido = "<p>Estimada Ana:</p><p>Ya hemos recibido toda su documentación.</p>";
        RevisionRespuestaSugerida.EsCasiLaMisma(
                RevisionRespuestaSugerida.TextoComparable(parecido), RevisionRespuestaSugerida.TextoComparable(TextoSugerido))
            .Should().BeTrue("el control: sin la asociación al destinatario, este cuerpo colisiona con la sugerencia de Ana");

        (await PorBuzon(conexion, parecido, destinatario: "luis@otra-contrata.test")).EsExitoso.Should().BeTrue();
        (await PorBuzon(conexion, parecido)).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        (await PorBuzon(conexion, parecido, destinatario: "ANA@Contrata.test")).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);

        (await EnviarNuevoA(conexion, parecido, ["luis@otra-contrata.test"])).EsExitoso.Should().BeTrue();
        _graph.VecesEnviado.Should().Be(1);
        (await EnviarNuevoA(conexion, parecido, ["luis@otra-contrata.test"], copia: [Ana]))
            .Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        _graph.VecesEnviado.Should().Be(1);
    }

    /// <summary>El remitente de un entrante también identifica al destinatario, aunque no esté como participante.</summary>
    [Fact]
    public async Task Por_buzon_el_remitente_de_un_entrante_cuenta_como_destinatario_del_hilo()
    {
        var conexion = Buzon();
        var conversacion = HiloDeCorreo(conexion);
        _comunicaciones.ListaParticipantesConversacion.Clear();
        Sugerencia(conversacion, EstadoRespuestaSugerida.Propuesta);

        (await PorBuzon(conexion, TextoSugerido)).EsExitoso.Should().BeTrue("sin participante ni entrante registrado, el hilo no es de Ana");

        EntranteDePersona(conversacion);
        (await PorBuzon(conexion, TextoSugerido)).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
    }

    private Task<Result<Guid>> EnviarNuevoA(
        ConexionIntegracion conexion, string cuerpo, IReadOnlyList<string> destinatarios, IReadOnlyList<string>? copia = null) =>
        new EnviarMensajeNuevoCommandHandler(
                _conexiones, _conversaciones, _comunicaciones, new AlcanceDatosServiceFalso(), new InvalidadorAlcanceInerte(), _graph,
                new AccesoGraphService(_credenciales, _graph), new FileStorageServiceFalso(),
                new ResolucionParticipanteConversacionServiceFalso(), _unidad, _registro)
            .Handle(new EnviarMensajeNuevoCommand(conexion.Id, destinatarios, "Documentación", cuerpo, Copia: copia), CancellationToken.None);

    [Fact]
    public async Task Por_buzon_un_texto_escrito_a_mano_o_una_edicion_por_dentro_si_sale()
    {
        var conexion = Buzon();
        Sugerencia(HiloDeCorreo(conexion), EstadoRespuestaSugerida.Propuesta);

        (await PorBuzon(conexion, EscritoAMano)).EsExitoso.Should().BeTrue();
        (await PorBuzon(conexion, EditadaMasDelUmbral)).EsExitoso.Should().BeTrue();
    }

    /// <summary>Revisión Codex r1 (#2163, ALTO 1), por buzón: la misma coincidencia por parecido que en una conversación.</summary>
    [Fact]
    public async Task Por_buzon_copiar_la_sugerencia_y_cambiar_una_letra_no_elude_el_bloqueo()
    {
        var conexion = Buzon();
        Sugerencia(HiloDeCorreo(conexion), EstadoRespuestaSugerida.Propuesta);

        (await PorBuzon(conexion, "<p>Estimada Ana:</p><p>Ya hemos recibido su documentacion.</p>"))
            .Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        (await EnviarNuevo(conexion, "<p>Estimada Ana:</p><p>Ya hemos recibido su documentacion.</p>"))
            .Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        _graph.VecesEnviado.Should().Be(0);
    }

    /// <summary>
    /// Un texto que no se parece a ningún otro de la serie: con la coincidencia por parecido, «número 8» y «número 9» con la
    /// misma frase están a una letra y se bloquearían entre sí. La huella (SHA-256 de <paramref name="i"/>) es fija y aparta
    /// cada texto de los demás mucho más que <see cref="RevisionRespuestaSugerida.UmbralEdicion"/>.
    /// </summary>
    private static string SinParecido(int i) =>
        $"<p>Sugerencia número {i}: {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(i)))}</p>";

    /// <summary>Fija el Id de la sugerencia: el orden de los lotes sale de él, y un Id al azar haría el test dependiente del azar.</summary>
    private static void FijarId(RespuestaSugerida respuesta, int orden) =>
        typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(respuesta, new Guid(orden, 0, 0, new byte[8]));

    /// <summary>
    /// Los textos se leen por lotes y la coincidencia cae en el último: 120 sugerencias sin parecido van por delante, con Id
    /// menores, y la consulta ordena por Id (la comprobación de orden es la del propio test). Con 121 candidatas hay 1
    /// lectura ligera y 3 de textos (50, 50 y 21). Como ninguna de las 120 anteriores contiene el cuerpo, que bloquee solo
    /// puede ser porque se leyó y comparó el tercer lote, que es el único donde está la candidata 121.
    /// </summary>
    [Fact]
    public async Task Por_buzon_con_muchas_sugerencias_detecta_la_coincidencia_en_el_ultimo_lote()
    {
        var conexion = Buzon();
        for (var i = 1; i <= 120; i++)
            FijarId(Sugerencia(HiloDeCorreo(conexion), EstadoRespuestaSugerida.Propuesta, SinParecido(i)), i);
        var coincidente = Sugerencia(HiloDeCorreo(conexion), EstadoRespuestaSugerida.Propuesta);
        FijarId(coincidente, 1000);

        _comunicaciones.ListaRespuestasSugeridas.OrderBy(r => r.Id).Last().Should().BeSameAs(coincidente);
        _comunicaciones.ListaRespuestasSugeridas.Should().HaveCount(121);

        var antes = _comunicaciones.LecturasDeRespuestasSugeridas;
        (await PorBuzon(conexion, CuerpoSinRelacion)).EsExitoso.Should().BeTrue();
        (_comunicaciones.LecturasDeRespuestasSugeridas - antes).Should().Be(4);

        (await PorBuzon(conexion, TextoSugerido + "<p>Un saludo</p>")).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
    }

    /// <summary>
    /// El historial no cierra el buzón: Revisada y Descartada del último entrante siguen siendo candidatas mientras la
    /// contraparte no contesta, y más de quinientas sin ninguna activa no impiden enviar. Una coincidencia real sigue bloqueando.
    /// </summary>
    [Fact]
    public async Task Por_buzon_el_historial_de_revisadas_y_descartadas_sin_activas_no_impide_enviar()
    {
        var conexion = Buzon();
        for (var i = 0; i < 520; i++)
        {
            var hilo = HiloDeCorreo(conexion);
            var estado = i % 2 == 0 ? EstadoRespuestaSugerida.Revisada : EstadoRespuestaSugerida.Descartada;
            Sugerencia(hilo, estado, SinParecido(i), EntranteDePersona(hilo));
        }

        (await PorBuzon(conexion, CuerpoSinRelacion)).EsExitoso.Should().BeTrue();
        (await EnviarNuevo(conexion, CuerpoSinRelacion)).EsExitoso.Should().BeTrue();
        _graph.VecesEnviado.Should().Be(1);

        var conCoincidencia = HiloDeCorreo(conexion);
        Sugerencia(conCoincidencia, EstadoRespuestaSugerida.Descartada, origen: EntranteDePersona(conCoincidencia));

        (await PorBuzon(conexion, TextoSugerido + "<p>Un saludo</p>")).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        // La 8 es una Revisada y nunca bloquea; la 9, una Descartada del último entrante, sí.
        (await PorBuzon(conexion, SinParecido(8) + "<p>Un saludo</p>")).EsExitoso.Should().BeTrue();
        (await PorBuzon(conexion, SinParecido(9) + "<p>Un saludo</p>"))
            .Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
    }

    private Conversacion HiloAnclado(ConexionIntegracion conexion, Guid? clienteId, Guid? empresaId)
    {
        var conversacion = new Conversacion("Documentación", clienteId, empresaId: empresaId);
        conversacion.AsociarConexion(conexion.Id, "hilo-" + Guid.NewGuid());
        conversacion.AgregarMensaje(
            DireccionMensaje.Entrante, CanalConversacion.Correo, Ana, "<p>Adjunto</p>", mensajeExternoId: "msg-1");
        // Como la ingesta: el remitente queda como participante del hilo, que es lo que mira el bloqueo por buzón.
        _comunicaciones.ListaParticipantesConversacion.Add(
            conversacion.AgregarParticipante(Ana, RolParticipante.De, TipoParticipanteOrigen.Empresa));
        _conversaciones.Agregar(conversacion);
        _comunicaciones.ListaConversaciones.Add(conversacion);
        return conversacion;
    }

    /// <summary>Lo que la consulta de candidatas tiene como origen: los hilos del buzón que deja pasar el filtro de alcance.</summary>
    private async Task<IReadOnlyList<Guid>> HilosQueEntranEnLaConsulta(ConexionIntegracion conexion, IAlcanceDatosService alcance)
    {
        var filtro = await BloqueoRespuestaSugerida.HilosVisiblesDelBuzonAsync(alcance, conexion.Id, CancellationToken.None);
        return filtro is null ? [] : _comunicaciones.Conversaciones.Where(filtro).Select(c => c.Id).ToList();
    }

    /// <summary>
    /// El filtro de la consulta es la regla de <c>ConversacionVisibleAsync</c> y no otra: se comparan sobre los mismos hilos
    /// (Cliente empresarial visible y no visible, sin ancla, Empresa contraparte en cartera y fuera de ella) con alcance
    /// total, con cartera, con cartera vacía y con el buzón no visible.
    /// </summary>
    [Fact]
    public async Task El_filtro_por_buzon_decide_igual_que_ConversacionVisibleAsync()
    {
        var conexion = Buzon();
        var clienteVisible = Guid.NewGuid();
        var empresaEnCartera = Guid.NewGuid();
        var hilos = new[]
        {
            HiloAnclado(conexion, clienteVisible, null),
            HiloAnclado(conexion, Guid.NewGuid(), null),
            HiloAnclado(conexion, null, null),
            HiloAnclado(conexion, null, empresaEnCartera),
            HiloAnclado(conexion, null, Guid.NewGuid())
        };
        var alcances = new Dictionary<string, IAlcanceDatosService>
        {
            ["total"] = new AlcanceDatosServiceFalso(),
            ["cartera"] = new AlcanceDatosServiceFalso(
                tieneAccesoTotal: false, clienteIdsVisibles: [clienteVisible], empresaIdsParaGestion: [empresaEnCartera]),
            ["vacia"] = new AlcanceDatosServiceFalso(tieneAccesoTotal: false),
            ["buzon no visible"] = new AlcanceDatosServiceFalso(conexionIntegracionVisible: false)
        };

        foreach (var (nombre, alcance) in alcances)
        {
            var esperados = new List<Guid>();
            foreach (var hilo in hilos)
            {
                if (await alcance.ConversacionVisibleAsync(hilo, CancellationToken.None))
                    esperados.Add(hilo.Id);
            }

            (await HilosQueEntranEnLaConsulta(conexion, alcance)).Should().BeEquivalentTo(esperados, nombre);
        }

        // Control de que la comparación no es vacía: con cartera entran el Cliente visible, el hilo sin ancla y la Empresa en cartera.
        (await HilosQueEntranEnLaConsulta(conexion, alcances["cartera"])).Should()
            .BeEquivalentTo([hilos[0].Id, hilos[2].Id, hilos[3].Id]);
        (await HilosQueEntranEnLaConsulta(conexion, alcances["buzon no visible"])).Should().BeEmpty();
    }

    /// <summary>
    /// Defensa en profundidad: el filtro de alcance va también en la consulta de textos. Un hilo reasignado a un ancla fuera
    /// de la cartera entre la consulta de candidatas (lectura 1) y la de los textos (lectura 2) no se compara: su texto sale
    /// del lote. Con una instantánea real esa reasignación no se vería (la base sirve la misma foto), pero el doble en
    /// memoria no modela los cambios de estado de las entidades, así que este test fija que la consulta de textos no se fía
    /// de que el Id esté entre las candidatas: sin el filtro devolvería el texto de un hilo que ya no es visible, la
    /// contención lo marcaría como coincidente y el envío se bloquearía por un hilo ajeno. El control sin reasignación sí
    /// bloquea, así que lo único que distingue los dos resultados es ese filtro.
    /// </summary>
    [Fact]
    public async Task Por_buzon_un_hilo_reasignado_fuera_de_cartera_entre_las_dos_lecturas_ni_bloquea_ni_se_compara()
    {
        var conexion = Buzon();
        var clienteEnCartera = Guid.NewGuid();
        var hilo = HiloAnclado(conexion, clienteEnCartera, null);
        Sugerencia(hilo, EstadoRespuestaSugerida.Propuesta);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [clienteEnCartera]);
        var cuerpo = TextoSugerido + "<p>Un saludo</p>";

        (await PorBuzon(conexion, cuerpo, alcance)).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);

        var antes = _comunicaciones.LecturasDeRespuestasSugeridas;
        _comunicaciones.AlLeerRespuestasSugeridas = lectura =>
        {
            if (lectura == antes + 2)
                hilo.AsignarCliente(Guid.NewGuid());
        };

        (await PorBuzon(conexion, cuerpo, alcance)).EsExitoso.Should().BeTrue();
        (_comunicaciones.LecturasDeRespuestasSugeridas - antes).Should().Be(2);
    }

    /// <summary>Sin ninguna coincidencia no hay lote de textos: una sola lectura, la de candidatas.</summary>
    [Fact]
    public async Task Por_buzon_sin_coincidencias_ni_textos_por_comparar_hay_una_sola_lectura()
    {
        var conexion = Buzon();
        Sugerencia(HiloDeCorreo(conexion), EstadoRespuestaSugerida.Revisada);
        var antes = _comunicaciones.LecturasDeRespuestasSugeridas;

        (await PorBuzon(conexion, CuerpoSinRelacion)).EsExitoso.Should().BeTrue();
        (_comunicaciones.LecturasDeRespuestasSugeridas - antes).Should().Be(1);
    }

    /// <summary>
    /// La Propuesta exacta tiene su Revisada (mismo hash y mensaje de origen) entre las candidatas de la misma instantánea,
    /// y la desbloquea sin más lecturas: la de candidatas y nada más.
    /// </summary>
    [Fact]
    public async Task Por_buzon_una_exacta_desbloqueada_por_su_revisada_se_decide_con_la_lectura_de_candidatas()
    {
        var conexion = Buzon();
        var conversacion = HiloDeCorreo(conexion);
        var entrante = EntranteDePersona(conversacion);
        Sugerencia(conversacion, EstadoRespuestaSugerida.Propuesta, origen: entrante);
        Sugerencia(conversacion, EstadoRespuestaSugerida.Revisada, origen: entrante);
        var antes = _comunicaciones.LecturasDeRespuestasSugeridas;

        (await PorBuzon(conexion, TextoSugerido)).EsExitoso.Should().BeTrue();

        (_comunicaciones.LecturasDeRespuestasSugeridas - antes).Should().Be(1);
    }

    /// <summary>
    /// Toda la comprobación va en una única instantánea: se abre una, ninguna consulta de Conversaciones, Mensajes o
    /// sugerencias ocurre fuera de ella, y los conjuntos de alcance se descartan y se leen dentro (antes de la primera
    /// lectura de datos). Se ejercitan las tres fases: candidatas, lote de textos y decisión.
    /// </summary>
    [Fact]
    public async Task Por_buzon_toda_la_comprobacion_se_ejecuta_dentro_de_una_unica_instantanea()
    {
        var conexion = Buzon();
        Sugerencia(HiloDeCorreo(conexion), EstadoRespuestaSugerida.Propuesta, "<p>Otra sugerencia que no se parece a nada de lo enviado.</p>");
        Sugerencia(HiloDeCorreo(conexion), EstadoRespuestaSugerida.Propuesta);
        var alcance = new AlcanceMemoizadoConRegistro(_comunicaciones, new AlcanceDatosServiceFalso());
        var instantaneasAntes = _comunicaciones.InstantaneasAbiertas;
        var fueraAntes = _comunicaciones.ConsultasFueraDeInstantanea;
        var lecturasAntes = _comunicaciones.LecturasDeRespuestasSugeridas;

        (await PorBuzon(conexion, TextoSugerido + "<p>Un saludo</p>", alcance, alcance)).Error
            .Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);

        (_comunicaciones.InstantaneasAbiertas - instantaneasAntes).Should().Be(1);
        (_comunicaciones.ConsultasFueraDeInstantanea - fueraAntes).Should().Be(0);
        (_comunicaciones.LecturasDeRespuestasSugeridas - lecturasAntes).Should().BeGreaterThan(1, "hubo candidatas y lote de textos");
        alcance.Invalidaciones.Should().Be(1);
        alcance.CargasFueraDeInstantanea.Should().Be(0);
        alcance.CargasDeCartera.Should().BeGreaterThan(0);
        _comunicaciones.DentroDeInstantanea.Should().BeFalse("la instantánea se cierra al terminar la comprobación");
    }

    /// <summary>
    /// Una sugerencia sin revisar con el mismo texto, confirmada por otra conexión después de abrir la instantánea (aquí,
    /// en la primera consulta, ya dentro), no se ve: es el límite aceptado, igual que un cambio posterior al envío. Es
    /// sensible a la instantánea: sin ella el doble no congelaría la lista, la candidata nueva entraría en la lectura y
    /// bloquearía. La misma sugerencia vista en una comprobación posterior sí bloquea. La semántica real de REPEATABLE READ
    /// la cubren los tests de integración; el doble solo congela qué sugerencias hay.
    /// </summary>
    [Fact]
    public async Task Por_buzon_una_sin_revisar_confirmada_despues_de_abrir_la_instantanea_no_se_ve_y_una_anterior_si()
    {
        var conexion = Buzon();
        var conversacion = HiloDeCorreo(conexion);
        Sugerencia(conversacion, EstadoRespuestaSugerida.Propuesta, "<p>Otra sugerencia que no se parece a nada de lo enviado.</p>");
        var cuerpo = TextoSugerido + "<p>Un saludo</p>";
        var antes = _comunicaciones.LecturasDeRespuestasSugeridas;
        _comunicaciones.AlLeerRespuestasSugeridas = lectura =>
        {
            // En la primera lectura, ya dentro de la instantánea: sin ella (lista sin congelar) la candidata nueva se vería y bloquearía.
            if (lectura == antes + 1)
                Sugerencia(HiloDeCorreo(conexion), EstadoRespuestaSugerida.SinRevisar);
        };

        (await PorBuzon(conexion, cuerpo)).EsExitoso.Should().BeTrue();
        (_comunicaciones.LecturasDeRespuestasSugeridas - antes).Should().Be(2);

        _comunicaciones.AlLeerRespuestasSugeridas = null;
        _comunicaciones.ListaRespuestasSugeridas.Should().HaveCount(2);
        (await PorBuzon(conexion, cuerpo)).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
    }

    /// <summary>
    /// La cartera revocada después de memoizarse se vuelve a leer dentro de la instantánea. El alcance de la petición ya se
    /// había calculado (con el hilo en cartera) antes de abrirla, y entre tanto otra sesión retira al Gestor CAE esa
    /// cartera. Con el invalidador inerte (la memoización de antes de abrir sigue valiendo) el hilo se ve y bloquea; con el
    /// invalidador real, la cartera se recalcula dentro, el hilo ya no es visible y no bloquea ni delata nada.
    /// </summary>
    [Fact]
    public async Task Por_buzon_la_cartera_revocada_tras_memoizarse_se_lee_de_nuevo_dentro_de_la_instantanea()
    {
        var conexion = Buzon();
        var clienteEnCartera = Guid.NewGuid();
        Sugerencia(HiloAnclado(conexion, clienteEnCartera, null), EstadoRespuestaSugerida.Propuesta);
        var cuerpo = TextoSugerido + "<p>Un saludo</p>";

        var alcance = new AlcanceMemoizadoConRegistro(
            _comunicaciones, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [clienteEnCartera]));
        await alcance.ObtenerClienteIdsVisiblesAsync();
        await alcance.ObtenerEmpresaIdsParaGestionAsync();
        alcance.Actual = new AlcanceDatosServiceFalso(tieneAccesoTotal: false);

        (await PorBuzon(conexion, cuerpo, alcance, new InvalidadorAlcanceInerte())).Error
            .Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar, "la memoización anterior sigue valiendo");
        (await PorBuzon(conexion, cuerpo, alcance, alcance)).EsExitoso.Should().BeTrue();
    }

    /// <summary>
    /// Alcance cuyos conjuntos de cartera se memorizan hasta <see cref="Invalidar"/>, como el servicio real de la petición, y
    /// que anota si se cargaron con una instantánea abierta.
    /// </summary>
    private sealed class AlcanceMemoizadoConRegistro(ComunicacionesQueryContextFalso comunicaciones, AlcanceDatosServiceFalso actual)
        : IAlcanceDatosService, IInvalidadorAlcance
    {
        private IReadOnlyList<Guid>? _clientes;
        private bool _clientesMemoizados;
        private IReadOnlyList<Guid>? _empresas;
        private bool _empresasMemoizadas;

        public AlcanceDatosServiceFalso Actual { get; set; } = actual;
        public int Invalidaciones { get; private set; }
        public int CargasDeCartera { get; private set; }
        public int CargasFueraDeInstantanea { get; private set; }

        public void Invalidar()
        {
            Invalidaciones++;
            _clientesMemoizados = false;
            _empresasMemoizadas = false;
        }

        private void Cargar()
        {
            CargasDeCartera++;
            if (!comunicaciones.DentroDeInstantanea)
                CargasFueraDeInstantanea++;
        }

        public async Task<IReadOnlyList<Guid>?> ObtenerClienteIdsVisiblesAsync(CancellationToken cancellationToken = default)
        {
            if (!_clientesMemoizados)
            {
                Cargar();
                _clientes = await Actual.ObtenerClienteIdsVisiblesAsync(cancellationToken);
                _clientesMemoizados = true;
            }
            return _clientes;
        }

        public async Task<IReadOnlyList<Guid>?> ObtenerEmpresaIdsParaGestionAsync(CancellationToken cancellationToken = default)
        {
            if (!_empresasMemoizadas)
            {
                Cargar();
                _empresas = await Actual.ObtenerEmpresaIdsParaGestionAsync(cancellationToken);
                _empresasMemoizadas = true;
            }
            return _empresas;
        }

        public Task<bool> ConexionIntegracionVisibleAsync(Guid conexionIntegracionId, CancellationToken cancellationToken = default) =>
            Actual.ConexionIntegracionVisibleAsync(conexionIntegracionId, cancellationToken);

        public Task<bool> TieneAccesoTotalAsync(CancellationToken cancellationToken = default) => Actual.TieneAccesoTotalAsync(cancellationToken);
        public Task<bool> OperaDesdeElLadoDeGestionAsync(CancellationToken cancellationToken = default) => Actual.OperaDesdeElLadoDeGestionAsync(cancellationToken);
        public Task<IReadOnlyList<Guid>?> ObtenerCentroIdsVisiblesAsync(CancellationToken cancellationToken = default) => Actual.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        public Task<IReadOnlyList<Guid>?> ObtenerCentroIdsParaGestionAsync(CancellationToken cancellationToken = default) => Actual.ObtenerCentroIdsParaGestionAsync(cancellationToken);
        public Task<IReadOnlyList<Guid>?> ObtenerEmpresaIdsVisiblesAsync(CancellationToken cancellationToken = default) => Actual.ObtenerEmpresaIdsVisiblesAsync(cancellationToken);
        public Task<IReadOnlyList<Guid>?> ObtenerSubcontrataIdsVisiblesAsync(CancellationToken cancellationToken = default) => Actual.ObtenerSubcontrataIdsVisiblesAsync(cancellationToken);
        public Task<IReadOnlyList<Guid>?> ObtenerSubcontrataIdsParaGestionAsync(CancellationToken cancellationToken = default) => Actual.ObtenerSubcontrataIdsParaGestionAsync(cancellationToken);
        public Task<bool> SubcontrataEliminadaParaGestionVisibleAsync(Guid subcontrataId, CancellationToken cancellationToken = default) => Actual.SubcontrataEliminadaParaGestionVisibleAsync(subcontrataId, cancellationToken);
        public Task<IReadOnlyList<Guid>?> ObtenerTrabajadorIdsVisiblesAsync(CancellationToken cancellationToken = default) => Actual.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        public Task<IReadOnlyList<Guid>?> ObtenerVehiculoIdsVisiblesAsync(CancellationToken cancellationToken = default) => Actual.ObtenerVehiculoIdsVisiblesAsync(cancellationToken);
    }

    [Fact]
    public async Task Por_buzon_un_hilo_anclado_a_una_Empresa_fuera_de_cartera_no_aporta_candidatas_ni_bloquea()
    {
        var conexion = Buzon();
        var fuera = HiloAnclado(conexion, null, Guid.NewGuid());
        Sugerencia(fuera, EstadoRespuestaSugerida.Propuesta);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsParaGestion: [Guid.NewGuid()]);

        (await HilosQueEntranEnLaConsulta(conexion, alcance)).Should().BeEmpty();
        (await PorBuzon(conexion, TextoSugerido, alcance)).EsExitoso.Should().BeTrue();
        (await PorBuzon(conexion, TextoSugerido + "<p>Un saludo</p>", alcance)).EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task Por_buzon_un_hilo_anclado_a_una_Empresa_en_cartera_bloquea()
    {
        var conexion = Buzon();
        var empresa = Guid.NewGuid();
        Sugerencia(HiloAnclado(conexion, null, empresa), EstadoRespuestaSugerida.Propuesta);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsParaGestion: [empresa]);

        (await HilosQueEntranEnLaConsulta(conexion, alcance)).Should().HaveCount(1);
        (await PorBuzon(conexion, TextoSugerido, alcance)).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        (await PorBuzon(conexion, TextoSugerido + "<p>Un saludo</p>", alcance)).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
    }

    [Fact]
    public async Task Por_buzon_con_el_buzon_no_visible_no_hay_candidatas_ni_se_lee_ninguna_sugerencia()
    {
        var conexion = Buzon();
        Sugerencia(HiloDeCorreo(conexion), EstadoRespuestaSugerida.Propuesta);
        var alcance = new AlcanceDatosServiceFalso(conexionIntegracionVisible: false);
        var antes = _comunicaciones.LecturasDeRespuestasSugeridas;

        (await PorBuzon(conexion, TextoSugerido, alcance)).EsExitoso.Should().BeTrue();

        (await HilosQueEntranEnLaConsulta(conexion, alcance)).Should().BeEmpty();
        (_comunicaciones.LecturasDeRespuestasSugeridas - antes).Should().Be(0);
    }

    /// <summary>
    /// El trabajo no depende de lo que haya en un hilo ajeno: con la sugerencia solo en un hilo que quien envía no ve, un
    /// cuerpo que la contiene y uno que no hacen exactamente las mismas lecturas (la ligera, y ninguna de textos).
    /// </summary>
    [Fact]
    public async Task Por_buzon_un_hilo_ajeno_no_provoca_lecturas_de_texto_ni_cambia_el_trabajo()
    {
        var conexion = Buzon();
        Sugerencia(HiloDeCorreo(conexion), EstadoRespuestaSugerida.Propuesta);
        var sinVer = new AlcanceDatosServiceFalso(tieneAccesoTotal: false);

        var antes = _comunicaciones.LecturasDeRespuestasSugeridas;
        (await PorBuzon(conexion, TextoSugerido + "<p>Un saludo</p>", sinVer)).EsExitoso.Should().BeTrue();
        var conCoincidencia = _comunicaciones.LecturasDeRespuestasSugeridas - antes;

        antes = _comunicaciones.LecturasDeRespuestasSugeridas;
        (await PorBuzon(conexion, CuerpoSinRelacion, sinVer)).EsExitoso.Should().BeTrue();
        var sinCoincidencia = _comunicaciones.LecturasDeRespuestasSugeridas - antes;

        conCoincidencia.Should().Be(1);
        sinCoincidencia.Should().Be(1);
        (await HilosQueEntranEnLaConsulta(conexion, sinVer)).Should().BeEmpty();
    }

    [Fact]
    public async Task Un_texto_con_menor_que_coincide_escrito_o_escapado()
    {
        var conversacion = HiloDeCorreo(Buzon());
        Sugerencia(conversacion, EstadoRespuestaSugerida.Propuesta, "<p>Vence si 5&lt;10</p>");

        (await Responder(conversacion, "Vence si 5<10")).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
    }
}
