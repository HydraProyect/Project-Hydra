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
            DireccionMensaje.Entrante, CanalConversacion.Correo, "ana@contrata.test", "<p>Adjunto</p>", mensajeExternoId: "msg-1");
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
                _conexiones, _conversaciones, _comunicaciones, new AlcanceDatosServiceFalso(), _graph,
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
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false), conexion.Id, TextoSugerido, CancellationToken.None);
        var viendolo = await _comunicaciones.ComprobarEnvioPorBuzonAsync(
            new AlcanceDatosServiceFalso(), conexion.Id, TextoSugerido, CancellationToken.None);

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
    public async Task Una_edicion_por_dentro_del_texto_si_sale(EstadoRespuestaSugerida estado)
    {
        var conversacion = HiloDeCorreo(Buzon());
        Sugerencia(conversacion, estado, origen: EntranteDePersona(conversacion));

        var resultado = await Responder(conversacion, "<p>Estimada Ana:</p><p>Ya hemos recibido toda su documentación.</p>");

        resultado.EsExitoso.Should().BeTrue();
        _graph.VecesEnviado.Should().Be(1);
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

    [Theory]
    [InlineData(BloqueoRespuestaSugerida.LongitudMinimaTextoContenido - 1, true)]
    [InlineData(BloqueoRespuestaSugerida.LongitudMinimaTextoContenido, false)]
    public async Task Un_texto_corto_solo_bloquea_por_igualdad(int longitud, bool sale)
    {
        var conversacion = HiloDeCorreo(Buzon());
        var texto = new string('a', longitud);
        Sugerencia(conversacion, EstadoRespuestaSugerida.Propuesta, texto);

        (await Responder(conversacion, texto + " Un saludo")).EsExitoso.Should().Be(sale);
        (await Responder(conversacion, texto)).EsExitoso.Should().BeFalse();
    }

    [Fact]
    public async Task Una_firma_corta_sobre_un_acuse_breve_no_elude_el_bloqueo()
    {
        var conversacion = HiloDeWhatsApp(DateTime.UtcNow.AddHours(-1), out _);
        Sugerencia(conversacion, EstadoRespuestaSugerida.Propuesta, "Recibido, muchas gracias.");

        (await ResponderPorWhatsApp(conversacion, "Recibido, muchas gracias. Marta")).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        _whatsApp.EnviosRealizados.Should().BeEmpty();
    }

    [Fact]
    public async Task Por_debajo_del_umbral_un_acuse_con_firma_si_sale()
    {
        var conversacion = HiloDeWhatsApp(DateTime.UtcNow.AddHours(-1), out _);
        Sugerencia(conversacion, EstadoRespuestaSugerida.Propuesta, "Ok, gracias");

        (await ResponderPorWhatsApp(conversacion, "Ok, gracias. Marta")).EsExitoso.Should().BeTrue();
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
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false), conexion.Id, TextoSugerido, CancellationToken.None);

        sinVer.EsExitoso.Should().BeTrue();
    }

    private const string CuerpoSinRelacion = "<p>Cuerpo sin relación alguna con ninguna sugerencia.</p>";

    private Task<Result> PorBuzon(ConexionIntegracion conexion, string cuerpo, IAlcanceDatosService? alcance = null) =>
        _comunicaciones.ComprobarEnvioPorBuzonAsync(alcance ?? new AlcanceDatosServiceFalso(), conexion.Id, cuerpo, CancellationToken.None);

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
    public async Task Por_buzon_un_texto_corto_solo_bloquea_por_igualdad(int longitud, bool sale)
    {
        var conexion = Buzon();
        var texto = new string('a', longitud);
        Sugerencia(HiloDeCorreo(conexion), EstadoRespuestaSugerida.Propuesta, texto);

        (await PorBuzon(conexion, texto + " Un saludo")).EsExitoso.Should().Be(sale);
        (await PorBuzon(conexion, texto)).EsExitoso.Should().BeFalse();
    }

    [Fact]
    public async Task Por_buzon_un_texto_escrito_a_mano_o_una_edicion_por_dentro_si_sale()
    {
        var conexion = Buzon();
        Sugerencia(HiloDeCorreo(conexion), EstadoRespuestaSugerida.Propuesta);

        (await PorBuzon(conexion, EscritoAMano)).EsExitoso.Should().BeTrue();
        (await PorBuzon(conexion, "<p>Estimada Ana:</p><p>Ya hemos recibido toda su documentación.</p>")).EsExitoso.Should().BeTrue();
    }

    /// <summary>La coincidencia en un lote que no es el primero también se detecta: los textos se leen por lotes.</summary>
    [Fact]
    public async Task Por_buzon_con_muchas_sugerencias_detecta_la_coincidencia_en_cualquier_lote()
    {
        var conexion = Buzon();
        for (var i = 0; i < 120; i++)
            Sugerencia(HiloDeCorreo(conexion), EstadoRespuestaSugerida.Propuesta, $"<p>Sugerencia número {i} sin parecido alguno.</p>");
        Sugerencia(HiloDeCorreo(conexion), EstadoRespuestaSugerida.Propuesta);

        (await PorBuzon(conexion, CuerpoSinRelacion)).EsExitoso.Should().BeTrue();
        (await PorBuzon(conexion, TextoSugerido + "<p>Un saludo</p>")).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
    }

    /// <summary>Con el tope alcanzado no se compara a medias: ni se deja pasar ni se confunde con una sugerencia sin revisar.</summary>
    [Fact]
    public async Task Por_buzon_con_el_tope_superado_falla_en_cerrado()
    {
        var conexion = Buzon();
        for (var i = 0; i < BloqueoRespuestaSugerida.TopeSugerenciasPorBuzon + 1; i++)
            Sugerencia(HiloDeCorreo(conexion), EstadoRespuestaSugerida.Propuesta, $"<p>Sugerencia número {i} sin parecido alguno.</p>");

        var resultado = await PorBuzon(conexion, CuerpoSinRelacion);

        resultado.Error.Should().Be(BloqueoRespuestaSugerida.DemasiadasSugerenciasEnElBuzon);
        (await EnviarNuevo(conexion, CuerpoSinRelacion)).Error.Should().Be(BloqueoRespuestaSugerida.DemasiadasSugerenciasEnElBuzon);
        _graph.VecesEnviado.Should().Be(0);
    }

    [Fact]
    public async Task Por_buzon_con_justo_el_tope_compara_con_normalidad()
    {
        var conexion = Buzon();
        for (var i = 0; i < BloqueoRespuestaSugerida.TopeSugerenciasPorBuzon; i++)
            Sugerencia(HiloDeCorreo(conexion), EstadoRespuestaSugerida.Propuesta, $"<p>Sugerencia número {i} sin parecido alguno.</p>");

        (await PorBuzon(conexion, CuerpoSinRelacion)).EsExitoso.Should().BeTrue();
        (await PorBuzon(conexion, "<p>Sugerencia número 7 sin parecido alguno.</p><p>Un saludo</p>"))
            .Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
    }

    [Fact]
    public async Task Un_texto_con_menor_que_coincide_escrito_o_escapado()
    {
        var conversacion = HiloDeCorreo(Buzon());
        Sugerencia(conversacion, EstadoRespuestaSugerida.Propuesta, "<p>Vence si 5&lt;10</p>");

        (await Responder(conversacion, "Vence si 5<10")).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
    }
}
