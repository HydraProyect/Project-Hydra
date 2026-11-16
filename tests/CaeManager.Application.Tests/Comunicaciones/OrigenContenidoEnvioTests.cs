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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaeManager.Application.Tests.Comunicaciones;

/// <summary>
/// Origen del texto y marca de IA en cada comando de envío (issue #2007, regla A): el id de la sugerencia de Vega que
/// insertó el redactor, validado en servidor; sin id, el texto comparado con las sugerencias de Vega revisadas de los
/// hilos visibles del buzón; si no, Persona. El doble de Graph guarda si se pidió la cabecera; ninguno de estos tests
/// saca correo de verdad.
/// </summary>
public class OrigenContenidoEnvioTests
{
    private const string TextoSugerido = "<p>Estimada Ana:</p><p>Ya hemos recibido su documentación.</p>";
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
    private readonly List<string> _trazas = [];

    /// <summary>Anota cada línea de log ya formateada, para comprobar la traza de origen tras un envío aceptado.</summary>
    private sealed class LoggerRecolector<T>(List<string> trazas) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            trazas.Add(formatter(state, exception));
    }

    private void TrazaDeOrigen(OrigenContenidoMensaje origen, Guid? respuestaSugeridaId) =>
        _trazas.Should().ContainSingle(t => t.StartsWith("Envío aceptado por", StringComparison.Ordinal))
            .Which.Should().Contain($"origen del contenido {origen}")
            .And.Contain($"respuesta sugerida {respuestaSugeridaId?.ToString() ?? "(null)"}.");

    private RespuestaSugerida Revisada(
        Conversacion conversacion, bool deVega = true, DateTime? revisadaEnUtc = null, string texto = TextoSugerido)
    {
        var sugerencia = Propuesta(conversacion, deVega, texto);
        sugerencia.Insertar();
        sugerencia.Confirmar(Guid.NewGuid(), revisadaEnUtc ?? DateTime.UtcNow);
        return sugerencia;
    }

    private RespuestaSugerida Propuesta(Conversacion conversacion, bool deVega = true, string texto = TextoSugerido)
    {
        var origen = UltimoEntrante(conversacion).Id;
        var sugerencia = deVega
            ? RespuestaSugerida.DeVega(conversacion.Id, origen, texto, Guid.NewGuid(), DateTime.UtcNow)
            : RespuestaSugerida.DesdeMacro(conversacion.Id, origen, Guid.NewGuid(), texto, Guid.NewGuid(), DateTime.UtcNow);
        _comunicaciones.ListaRespuestasSugeridas.Add(sugerencia);
        return sugerencia;
    }

    private static Mensaje UltimoEntrante(Conversacion conversacion) =>
        conversacion.Mensajes.Where(m => m.Direccion == DireccionMensaje.Entrante).OrderByDescending(m => m.FechaUtc).First();

    private static Mensaje UltimoSaliente(Conversacion conversacion) =>
        conversacion.Mensajes.Last(m => m.Direccion == DireccionMensaje.Saliente);

    private ConexionIntegracion Buzon()
    {
        var conexion = new ConexionIntegracion("cae@empresa.test", "Buzón CAE").Verificada();
        _conexiones.Agregar(conexion);
        _integraciones.ConexionesLista.Add(conexion);
        _credenciales.Agregar(new CredencialIntegracion(conexion.Id, "refresh-token"));
        return conexion;
    }

    private Conversacion HiloDeCorreo(ConexionIntegracion conexion, Guid? clienteId = null, string remitente = Ana)
    {
        var conversacion = new Conversacion("Documentación", clienteId: clienteId ?? Guid.NewGuid());
        conversacion.AsociarConexion(conexion.Id, "hilo-" + Guid.NewGuid());
        _comunicaciones.ListaMensajes.Add(conversacion.AgregarMensaje(
            DireccionMensaje.Entrante, CanalConversacion.Correo, remitente, "<p>Adjunto</p>", DateTime.UtcNow.AddMinutes(-10),
            "msg-" + Guid.NewGuid()));
        _conversaciones.Agregar(conversacion);
        _comunicaciones.ListaConversaciones.Add(conversacion);
        return conversacion;
    }

    private Conversacion HiloDeWhatsApp(DateTime ultimoEntranteUtc)
    {
        var linea = new ConexionIntegracion("+34686543364", "Línea", proveedor: ProveedorIntegracion.WhatsApp);
        _conexiones.Agregar(linea);
        _lineas.Agregar(new LineaWhatsApp(
            linea.Id, "111222333", "waba-1", "+34686543364", "token", ModoAsignacionLinea.GestorFijo, Guid.NewGuid()));

        var conversacion = Conversacion.CrearWhatsApp(Telefono, linea.Id, null, Guid.NewGuid());
        _comunicaciones.ListaMensajes.Add(conversacion.AgregarMensaje(
            DireccionMensaje.Entrante, CanalConversacion.WhatsApp, Telefono, "Hola", ultimoEntranteUtc, "wamid.in"));
        _conversaciones.Agregar(conversacion);
        _comunicaciones.ListaConversaciones.Add(conversacion);
        return conversacion;
    }

    private Task<Result> Responder(
        Conversacion conversacion, string cuerpo, Guid? respuestaSugeridaId = null, IAlcanceDatosService? alcance = null) =>
        new ResponderConversacionCommandHandler(
                _conversaciones, _comunicaciones, _conexiones, alcance ?? new AlcanceDatosServiceFalso(), _graph,
                new AccesoGraphService(_credenciales, _graph), new FileStorageServiceFalso(),
                Options.Create(new ComunicacionesRemitenteOptions()), _unidad, new LoggerRecolector<ResponderConversacionCommandHandler>(_trazas),
                _registro, new DescarteCambiosPendientesFalso())
            .Handle(new ResponderConversacionCommand(conversacion.Id, cuerpo, RespuestaSugeridaId: respuestaSugeridaId), CancellationToken.None);

    private Task<Result> ResponderPorWhatsApp(Conversacion conversacion, string texto, Guid? respuestaSugeridaId = null) =>
        new ResponderConversacionWhatsAppCommandHandler(
                _conversaciones, _comunicaciones, _conexiones, _lineas, new AlcanceDatosServiceFalso(), _whatsApp, _unidad,
                NullLogger<ResponderConversacionWhatsAppCommandHandler>.Instance, _registro, new DescarteCambiosPendientesFalso())
            .Handle(new ResponderConversacionWhatsAppCommand(conversacion.Id, texto, respuestaSugeridaId), CancellationToken.None);

    private Task<Result> MigrarACorreo(Conversacion conversacion, string cuerpo, Guid? respuestaSugeridaId = null) =>
        new MigrarConversacionACorreoCommandHandler(
                _conversaciones, _comunicaciones, _integraciones, _conexiones, new AlcanceDatosServiceFalso(), _graph,
                new AccesoGraphService(_credenciales, _graph), new ResolucionParticipanteConversacionServiceFalso(), _unidad, _registro,
                new LoggerRecolector<MigrarConversacionACorreoCommandHandler>(_trazas))
            .Handle(new MigrarConversacionACorreoCommand(conversacion.Id, Ana, cuerpo, respuestaSugeridaId), CancellationToken.None);

    private Task<Result<Guid>> EnviarNuevo(ConexionIntegracion conexion, string cuerpo) =>
        new EnviarMensajeNuevoCommandHandler(
                _conexiones, _conversaciones, _comunicaciones, new AlcanceDatosServiceFalso(), new InvalidadorAlcanceInerte(), _graph,
                new AccesoGraphService(_credenciales, _graph), new FileStorageServiceFalso(),
                new ResolucionParticipanteConversacionServiceFalso(), _unidad, _registro,
                new LoggerRecolector<EnviarMensajeNuevoCommandHandler>(_trazas))
            .Handle(new EnviarMensajeNuevoCommand(conexion.Id, [Ana], "Documentación", cuerpo), CancellationToken.None);

    private void SinEnvio(Conversacion conversacion)
    {
        _graph.UltimaMarcaIaRevisada.Should().BeNull("no se llamó a Graph");
        conversacion.Mensajes.Should().NotContain(m => m.Direccion == DireccionMensaje.Saliente);
    }

    // ---- 1. Con el id del redactor ----

    [Fact]
    public async Task Con_el_id_de_una_sugerencia_de_Vega_revisada_pide_la_cabecera_y_guarda_VegaRevisada()
    {
        var conversacion = HiloDeCorreo(Buzon());
        var sugerencia = Revisada(conversacion);

        var resultado = await Responder(conversacion, TextoSugerido, sugerencia.Id);

        resultado.EsExitoso.Should().BeTrue();
        _graph.UltimaMarcaIaRevisada.Should().BeTrue();
        var mensaje = UltimoSaliente(conversacion);
        mensaje.OrigenContenido.Should().Be(OrigenContenidoMensaje.VegaRevisada);
        mensaje.RespuestaSugeridaId.Should().Be(sugerencia.Id);
        // Sin texto visible añadido: el cuerpo que sale es el que revisó la persona.
        _graph.UltimoCuerpoRespondido.Should().Be(TextoSugerido);
    }

    /// <summary>Con el id no hace falta parecido: el texto salió del redactor con la sugerencia de Vega dentro.</summary>
    [Fact]
    public async Task Con_el_id_un_texto_de_Vega_muy_editado_sigue_marcado_como_de_IA()
    {
        var conversacion = HiloDeCorreo(Buzon());
        var sugerencia = Revisada(conversacion);

        await Responder(conversacion, "<p>Hola Ana, te escribo yo con otras palabras y bastante más contexto del caso.</p>", sugerencia.Id);

        _graph.UltimaMarcaIaRevisada.Should().BeTrue();
        UltimoSaliente(conversacion).OrigenContenido.Should().Be(OrigenContenidoMensaje.VegaRevisada);
    }

    /// <summary>
    /// Decisión de Chris del 2026-11-16 (#2007), rama de descarte: la persona descarta la sugerencia de Vega que insertó,
    /// se queda con el texto editado y lo envía. El origen de IA se conserva porque el texto siguió en el redactor.
    /// </summary>
    [Fact]
    public async Task Con_el_id_de_una_sugerencia_de_Vega_descartada_tras_insertarla_sigue_marcado_como_de_IA()
    {
        var conversacion = HiloDeCorreo(Buzon());
        var sugerencia = Propuesta(conversacion);
        sugerencia.Insertar();
        sugerencia.Descartar();

        var resultado = await Responder(
            conversacion, "<p>Hola Ana, te escribo yo con otras palabras y bastante más contexto del caso.</p>", sugerencia.Id);

        resultado.EsExitoso.Should().BeTrue();
        _graph.UltimaMarcaIaRevisada.Should().BeTrue();
        var mensaje = UltimoSaliente(conversacion);
        mensaje.OrigenContenido.Should().Be(OrigenContenidoMensaje.VegaRevisada);
        mensaje.RespuestaSugeridaId.Should().Be(sugerencia.Id);
    }

    /// <summary>
    /// Decisión de Chris del 2026-11-16 (#2007), rama de rechazo: otra sesión descarta la sugerencia, o el panel la
    /// sustituye por otra generación, mientras su texto sigue en este redactor. Antes el servidor rechazaba el id y el
    /// redactor lo olvidaba, de modo que el reintento salía como Persona; ahora el envío sale con el origen de IA.
    /// </summary>
    [Fact]
    public async Task Con_el_id_de_una_sugerencia_de_Vega_sustituida_por_otra_generacion_sigue_marcado_como_de_IA()
    {
        var conversacion = HiloDeCorreo(Buzon());
        var sustituida = Propuesta(conversacion);
        sustituida.Insertar();
        sustituida.Descartar();
        Propuesta(conversacion, texto: "<p>Una generación nueva que todavía nadie ha insertado en el redactor.</p>");

        var resultado = await Responder(
            conversacion, "<p>Hola Ana, te escribo yo con otras palabras y bastante más contexto del caso.</p>", sustituida.Id);

        resultado.EsExitoso.Should().BeTrue();
        _graph.UltimaMarcaIaRevisada.Should().BeTrue();
        UltimoSaliente(conversacion).RespuestaSugeridaId.Should().Be(sustituida.Id);
    }

    [Fact]
    public async Task El_id_de_una_macro_se_rechaza_sin_enviar()
    {
        var conversacion = HiloDeCorreo(Buzon());
        var macro = Revisada(conversacion, deVega: false);

        var resultado = await Responder(conversacion, TextoSugerido, macro.Id);

        resultado.Error.Should().Be(OrigenContenidoEnvio.SugerenciaNoUtilizable);
        SinEnvio(conversacion);
    }

    [Fact]
    public async Task El_id_de_una_sugerencia_de_Vega_sin_revisar_se_rechaza_sin_enviar()
    {
        var conversacion = HiloDeCorreo(Buzon());
        var propuesta = Propuesta(conversacion, texto: "<p>Una propuesta que nadie ha revisado todavía en este hilo.</p>");

        var resultado = await Responder(conversacion, "<p>Te contesto yo.</p>", propuesta.Id);

        resultado.Error.Should().Be(OrigenContenidoEnvio.SugerenciaNoUtilizable);
        SinEnvio(conversacion);
    }

    [Fact]
    public async Task Un_id_que_no_existe_se_rechaza_sin_enviar()
    {
        var conversacion = HiloDeCorreo(Buzon());

        var resultado = await Responder(conversacion, TextoSugerido, Guid.NewGuid());

        resultado.Error.Should().Be(OrigenContenidoEnvio.SugerenciaNoUtilizable);
        SinEnvio(conversacion);
    }

    /// <summary>Regla A: la sugerencia del id pertenece a un hilo que quien envía ve; uno fuera de su cartera se rechaza.</summary>
    [Fact]
    public async Task El_id_de_una_sugerencia_de_un_hilo_no_visible_se_rechaza_sin_enviar()
    {
        var buzon = Buzon();
        var clienteVisible = Guid.NewGuid();
        var conversacion = HiloDeCorreo(buzon, clienteVisible);
        var ajena = Revisada(HiloDeCorreo(buzon, Guid.NewGuid()));
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [clienteVisible]);

        var resultado = await Responder(conversacion, TextoSugerido, ajena.Id, alcance);

        resultado.Error.Should().Be(OrigenContenidoEnvio.SugerenciaNoUtilizable);
        SinEnvio(conversacion);
    }

    [Fact]
    public async Task El_id_de_una_sugerencia_de_Vega_de_otro_hilo_visible_vale()
    {
        var buzon = Buzon();
        var conversacion = HiloDeCorreo(buzon);
        var deOtroHilo = Revisada(HiloDeCorreo(buzon));

        var resultado = await Responder(conversacion, TextoSugerido, deOtroHilo.Id);

        resultado.EsExitoso.Should().BeTrue();
        UltimoSaliente(conversacion).RespuestaSugeridaId.Should().Be(deOtroHilo.Id);
        _graph.UltimaMarcaIaRevisada.Should().BeTrue();
    }

    // ---- 2. Sin id: el texto ----

    [Fact]
    public async Task Sin_id_el_texto_de_una_sugerencia_de_Vega_revisada_del_hilo_lleva_la_cabecera()
    {
        var conversacion = HiloDeCorreo(Buzon());
        var sugerencia = Revisada(conversacion);

        await Responder(conversacion, TextoSugerido + "<p>Un saludo.</p>");

        _graph.UltimaMarcaIaRevisada.Should().BeTrue();
        UltimoSaliente(conversacion).OrigenContenido.Should().Be(OrigenContenidoMensaje.VegaRevisada);
        UltimoSaliente(conversacion).RespuestaSugeridaId.Should().Be(sugerencia.Id);
    }

    /// <summary>Revisión Codex r2 de la #2007 (alta): copiar la propuesta de Vega de otro hilo del buzón no sale como Persona.</summary>
    [Fact]
    public async Task Sin_id_el_texto_de_Vega_de_otro_hilo_visible_del_buzon_lleva_la_cabecera()
    {
        var buzon = Buzon();
        var conversacion = HiloDeCorreo(buzon, remitente: "luis@otra.test");
        var deOtroHilo = Revisada(HiloDeCorreo(buzon));

        await Responder(conversacion, TextoSugerido);

        _graph.UltimaMarcaIaRevisada.Should().BeTrue();
        UltimoSaliente(conversacion).RespuestaSugeridaId.Should().Be(deOtroHilo.Id);
    }

    [Fact]
    public async Task Sin_id_el_texto_de_Vega_de_un_hilo_no_visible_no_cuenta()
    {
        var buzon = Buzon();
        var clienteVisible = Guid.NewGuid();
        var conversacion = HiloDeCorreo(buzon, clienteVisible);
        Revisada(HiloDeCorreo(buzon, Guid.NewGuid()));
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [clienteVisible]);

        await Responder(conversacion, TextoSugerido, alcance: alcance);

        _graph.UltimaMarcaIaRevisada.Should().BeFalse();
        UltimoSaliente(conversacion).OrigenContenido.Should().Be(OrigenContenidoMensaje.Persona);
    }

    /// <summary>
    /// Revisión Codex r4: si el hilo sale de la cartera de quien envía entre la lectura de los ids y la del lote de textos,
    /// su sugerencia ya no se compara. La primera lectura de sugerencias es la de los ids; la segunda, la del lote.
    /// </summary>
    [Fact]
    public async Task Sin_id_una_sugerencia_cuyo_hilo_sale_de_la_cartera_antes_de_leer_el_lote_no_cuenta()
    {
        var buzon = Buzon();
        var clienteVisible = Guid.NewGuid();
        var otroHilo = HiloDeCorreo(buzon, clienteVisible);
        Revisada(otroHilo);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [clienteVisible]);
        _comunicaciones.AlLeerRespuestasSugeridas = lectura =>
        {
            if (lectura == 2)
                otroHilo.AsignarCliente(Guid.NewGuid());
        };

        var origen = await _comunicaciones.OrigenDelEnvioAsync(
            alcance, respuestaSugeridaId: null, conversacionId: null, buzon.Id, TextoSugerido, CancellationToken.None);

        _comunicaciones.LecturasDeRespuestasSugeridas.Should().Be(2, "una lectura de ids y una de lote");
        origen.Valor.Should().BeNull();
    }

    /// <summary>Control del anterior: sin la reasignación, la misma sugerencia sí cuenta.</summary>
    [Fact]
    public async Task Sin_id_una_sugerencia_de_un_hilo_que_sigue_en_cartera_cuenta()
    {
        var buzon = Buzon();
        var clienteVisible = Guid.NewGuid();
        var sugerencia = Revisada(HiloDeCorreo(buzon, clienteVisible));
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [clienteVisible]);

        var origen = await _comunicaciones.OrigenDelEnvioAsync(
            alcance, respuestaSugeridaId: null, conversacionId: null, buzon.Id, TextoSugerido, CancellationToken.None);

        origen.Valor!.Id.Should().Be(sugerencia.Id);
    }

    /// <summary>Regla A: se elimina la heurística de «no consumida»; una propuesta de Vega revisada no marca un texto distinto.</summary>
    [Fact]
    public async Task Sin_id_un_texto_distinto_de_la_sugerencia_de_Vega_revisada_es_Persona()
    {
        var conversacion = HiloDeCorreo(Buzon());
        Revisada(conversacion);

        await Responder(conversacion, "<p>Hola Ana, te escribo yo con otras palabras y bastante más contexto del caso.</p>");

        _graph.UltimaMarcaIaRevisada.Should().BeFalse();
        UltimoSaliente(conversacion).OrigenContenido.Should().Be(OrigenContenidoMensaje.Persona);
        UltimoSaliente(conversacion).RespuestaSugeridaId.Should().BeNull();
    }

    [Fact]
    public async Task Sin_id_el_texto_de_una_macro_revisada_es_Persona_sin_cabecera()
    {
        var conversacion = HiloDeCorreo(Buzon());
        Revisada(conversacion, deVega: false);

        await Responder(conversacion, TextoSugerido);

        _graph.UltimaMarcaIaRevisada.Should().BeFalse();
        UltimoSaliente(conversacion).OrigenContenido.Should().Be(OrigenContenidoMensaje.Persona);
    }

    [Fact]
    public async Task Responder_sin_sugerencia_es_Persona_sin_cabecera()
    {
        var conversacion = HiloDeCorreo(Buzon());

        await Responder(conversacion, "<p>Te contesto yo.</p>");

        _graph.UltimaMarcaIaRevisada.Should().BeFalse();
        UltimoSaliente(conversacion).OrigenContenido.Should().Be(OrigenContenidoMensaje.Persona);
        UltimoSaliente(conversacion).RespuestaSugeridaId.Should().BeNull();
    }

    /// <summary>Revisión Codex de la #2007: reenviar el texto de Vega ya enviado sigue siendo texto de Vega.</summary>
    [Fact]
    public async Task Reenviar_el_texto_de_una_sugerencia_de_Vega_ya_enviada_vuelve_a_marcarlo()
    {
        var conversacion = HiloDeCorreo(Buzon());
        var sugerencia = Revisada(conversacion);
        await Responder(conversacion, TextoSugerido, sugerencia.Id);
        _comunicaciones.ListaMensajes.Add(UltimoSaliente(conversacion));

        await Responder(conversacion, TextoSugerido);

        _graph.UltimaMarcaIaRevisada.Should().BeTrue();
        UltimoSaliente(conversacion).RespuestaSugeridaId.Should().Be(sugerencia.Id);
    }

    [Fact]
    public async Task Con_dos_revisadas_de_Vega_vale_la_que_coincide_con_el_cuerpo()
    {
        var conversacion = HiloDeCorreo(Buzon());
        var coincide = Revisada(conversacion, revisadaEnUtc: DateTime.UtcNow.AddMinutes(-5));
        Revisada(conversacion, revisadaEnUtc: DateTime.UtcNow, texto: "<p>Otro texto distinto que nadie va a enviar hoy.</p>");

        await Responder(conversacion, TextoSugerido);

        UltimoSaliente(conversacion).RespuestaSugeridaId.Should().Be(coincide.Id);
    }

    /// <summary>Si coinciden varias, vale la revisada más tarde, aunque se lea antes la otra.</summary>
    [Fact]
    public async Task Con_dos_revisadas_de_Vega_que_coinciden_vale_la_revisada_mas_tarde()
    {
        var conversacion = HiloDeCorreo(Buzon());
        var reciente = Revisada(conversacion, revisadaEnUtc: DateTime.UtcNow);
        Revisada(conversacion, revisadaEnUtc: DateTime.UtcNow.AddMinutes(-5));

        await Responder(conversacion, TextoSugerido);

        UltimoSaliente(conversacion).RespuestaSugeridaId.Should().Be(reciente.Id);
    }

    // ---- Traza de origen en cuanto el canal acepta el envío (revisión Codex r3) ----

    [Fact]
    public async Task Responder_deja_la_traza_de_origen_al_aceptar_Graph_el_envio()
    {
        var conversacion = HiloDeCorreo(Buzon());
        var sugerencia = Revisada(conversacion);

        await Responder(conversacion, TextoSugerido, sugerencia.Id);

        TrazaDeOrigen(OrigenContenidoMensaje.VegaRevisada, sugerencia.Id);
        // Con el Id del correo que salió: dos respuestas del mismo hilo se distinguen en la traza (revisión Codex r4).
        _trazas.Should().Contain(t => t.Contains($"mensaje externo {Microsoft365GraphClientFalso.IdRespuestaConMarca},"));
    }

    /// <summary>
    /// Revisión Codex r4: la respuesta con marca vuelve a comprobar el buzón justo antes de enviar. La primera relectura
    /// es la del comando, antes de llamar a Graph; la segunda, la puerta entre crear el borrador y enviarlo.
    /// </summary>
    [Fact]
    public async Task Responder_con_marca_no_sale_si_el_buzon_deja_de_estar_operativo_antes_del_envio()
    {
        var conexion = Buzon();
        var conversacion = HiloDeCorreo(conexion);
        var sugerencia = Revisada(conversacion);
        _conexiones.AlReleer = vez =>
        {
            if (vez == 2)
                _conexiones.NoOperativasEnBase.Add(conexion.Id);
        };

        var resultado = await Responder(conversacion, TextoSugerido, sugerencia.Id);

        resultado.Error.Codigo.Should().Be(ErroresBuzonSinVerificar.Crear().Codigo);
        _conexiones.VecesReleido.Should().Be(2);
        SinEnvio(conversacion);
    }

    /// <summary>El correo ya salió cuando falla el guardado: la traza queda aunque no quede el Mensaje.</summary>
    [Fact]
    public async Task Al_migrar_a_correo_la_traza_de_origen_queda_aunque_falle_el_guardado()
    {
        var conversacion = HiloDeWhatsApp(DateTime.UtcNow.AddDays(-2));
        var sugerencia = Revisada(conversacion);
        Buzon();
        _unidad.ExcepcionAlGuardar = new InvalidOperationException("la base se cayó");

        var migrar = () => MigrarACorreo(conversacion, TextoSugerido, sugerencia.Id);

        await migrar.Should().ThrowAsync<InvalidOperationException>();
        TrazaDeOrigen(OrigenContenidoMensaje.VegaRevisada, sugerencia.Id);
    }

    [Fact]
    public async Task Un_mensaje_nuevo_deja_la_traza_de_origen_aunque_falle_el_guardado()
    {
        var conexion = Buzon();
        var sugerencia = Revisada(HiloDeCorreo(conexion));
        _unidad.ExcepcionAlGuardar = new InvalidOperationException("la base se cayó");

        var enviar = () => EnviarNuevo(conexion, TextoSugerido);

        await enviar.Should().ThrowAsync<InvalidOperationException>();
        TrazaDeOrigen(OrigenContenidoMensaje.VegaRevisada, sugerencia.Id);
    }

    [Fact]
    public async Task Un_mensaje_nuevo_escrito_a_mano_deja_la_traza_como_Persona()
    {
        var conexion = Buzon();

        await EnviarNuevo(conexion, "<p>Te escribo yo, sin ninguna sugerencia de por medio.</p>");

        TrazaDeOrigen(OrigenContenidoMensaje.Persona, null);
    }

    [Fact]
    public async Task Un_envio_que_falla_en_Graph_no_registra_mensaje()
    {
        var conversacion = HiloDeCorreo(Buzon());
        var sugerencia = Revisada(conversacion);
        _graph.FallaEnvio = true;

        var resultado = await Responder(conversacion, TextoSugerido, sugerencia.Id);

        resultado.EsFallido.Should().BeTrue();
        conversacion.Mensajes.Should().NotContain(m => m.Direccion == DireccionMensaje.Saliente);
    }

    // ---- WhatsApp, migración a correo y mensaje nuevo ----

    [Fact]
    public async Task Por_WhatsApp_con_el_id_el_origen_queda_en_el_mensaje()
    {
        var conversacion = HiloDeWhatsApp(DateTime.UtcNow.AddHours(-1));
        var sugerencia = Revisada(conversacion);

        var resultado = await ResponderPorWhatsApp(conversacion, "Ya hemos recibido su documentación.", sugerencia.Id);

        resultado.EsExitoso.Should().BeTrue();
        UltimoSaliente(conversacion).OrigenContenido.Should().Be(OrigenContenidoMensaje.VegaRevisada);
        UltimoSaliente(conversacion).RespuestaSugeridaId.Should().Be(sugerencia.Id);
    }

    [Fact]
    public async Task Por_WhatsApp_sin_id_cuenta_el_texto()
    {
        var conversacion = HiloDeWhatsApp(DateTime.UtcNow.AddHours(-1));
        var sugerencia = Revisada(conversacion, texto: "Ya hemos recibido su documentación, gracias.");

        await ResponderPorWhatsApp(conversacion, "Ya hemos recibido su documentación, gracias.");

        UltimoSaliente(conversacion).RespuestaSugeridaId.Should().Be(sugerencia.Id);
    }

    [Fact]
    public async Task Por_WhatsApp_un_id_no_valido_se_rechaza_sin_enviar()
    {
        var conversacion = HiloDeWhatsApp(DateTime.UtcNow.AddHours(-1));
        var macro = Revisada(conversacion, deVega: false);

        var resultado = await ResponderPorWhatsApp(conversacion, "Ya hemos recibido su documentación.", macro.Id);

        resultado.Error.Should().Be(OrigenContenidoEnvio.SugerenciaNoUtilizable);
        conversacion.Mensajes.Should().NotContain(m => m.Direccion == DireccionMensaje.Saliente);
    }

    [Fact]
    public async Task Al_migrar_a_correo_con_el_id_la_sugerencia_de_Vega_lleva_la_cabecera()
    {
        Buzon();
        var conversacion = HiloDeWhatsApp(DateTime.UtcNow.AddDays(-2));
        var sugerencia = Revisada(conversacion);

        var resultado = await MigrarACorreo(conversacion, "<p>Te escribo por correo con mis palabras.</p>", sugerencia.Id);

        resultado.EsExitoso.Should().BeTrue();
        _graph.UltimaMarcaIaRevisada.Should().BeTrue();
        var mensaje = conversacion.Mensajes.Last(m => m.Canal == CanalConversacion.Correo);
        mensaje.OrigenContenido.Should().Be(OrigenContenidoMensaje.VegaRevisada);
        mensaje.RespuestaSugeridaId.Should().Be(sugerencia.Id);
    }

    [Fact]
    public async Task Al_migrar_a_correo_un_id_no_valido_se_rechaza_sin_enviar()
    {
        Buzon();
        var conversacion = HiloDeWhatsApp(DateTime.UtcNow.AddDays(-2));

        var resultado = await MigrarACorreo(conversacion, TextoSugerido, Guid.NewGuid());

        resultado.Error.Should().Be(OrigenContenidoEnvio.SugerenciaNoUtilizable);
        SinEnvio(conversacion);
    }

    [Fact]
    public async Task Al_migrar_a_correo_sin_id_cuenta_el_texto()
    {
        Buzon();
        var conversacion = HiloDeWhatsApp(DateTime.UtcNow.AddDays(-2));
        var sugerencia = Revisada(conversacion);

        await MigrarACorreo(conversacion, TextoSugerido);

        _graph.UltimaMarcaIaRevisada.Should().BeTrue();
        conversacion.Mensajes.Last(m => m.Canal == CanalConversacion.Correo).RespuestaSugeridaId.Should().Be(sugerencia.Id);
    }

    [Fact]
    public async Task Un_mensaje_nuevo_escrito_a_mano_es_Persona_sin_cabecera()
    {
        var conexion = Buzon();

        var resultado = await EnviarNuevo(conexion, TextoSugerido);

        resultado.EsExitoso.Should().BeTrue();
        _graph.UltimaMarcaIaRevisada.Should().BeFalse();
        var mensaje = _conversaciones.Conversaciones.SelectMany(c => c.Mensajes).Single(m => m.Direccion == DireccionMensaje.Saliente);
        mensaje.OrigenContenido.Should().Be(OrigenContenidoMensaje.Persona);
    }

    /// <summary>
    /// Revisión Codex r2 de la #2007 (alta): el texto de una propuesta de Vega revisada de cualquier hilo visible del
    /// buzón, también con otro destinatario, lleva la marca en un mensaje nuevo.
    /// </summary>
    [Fact]
    public async Task Un_mensaje_nuevo_con_el_texto_de_Vega_de_un_hilo_con_otro_destinatario_lleva_la_cabecera()
    {
        var conexion = Buzon();
        var sugerencia = Revisada(HiloDeCorreo(conexion, remitente: "luis@otra.test"));

        var resultado = await EnviarNuevo(conexion, TextoSugerido);

        resultado.EsExitoso.Should().BeTrue();
        _graph.UltimaMarcaIaRevisada.Should().BeTrue();
        var mensaje = _conversaciones.Conversaciones.SelectMany(c => c.Mensajes)
            .Single(m => m.Direccion == DireccionMensaje.Saliente);
        mensaje.OrigenContenido.Should().Be(OrigenContenidoMensaje.VegaRevisada);
        mensaje.RespuestaSugeridaId.Should().Be(sugerencia.Id);
    }
}
