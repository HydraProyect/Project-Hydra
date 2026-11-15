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
/// Origen del texto y marca de IA en cada comando de envío (issue #2007): lo resuelve el servidor desde la sugerencia
/// revisada del último entrante que todavía no originó ningún mensaje. El doble de Graph guarda si se pidió la cabecera;
/// ninguno de estos tests saca correo de verdad.
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

    private RespuestaSugerida Revisada(Conversacion conversacion, bool deVega = true, Guid? mensajeOrigenId = null, DateTime? revisadaEnUtc = null)
    {
        var origen = mensajeOrigenId ?? UltimoEntrante(conversacion).Id;
        var sugerencia = deVega
            ? RespuestaSugerida.DeVega(conversacion.Id, origen, TextoSugerido, Guid.NewGuid(), DateTime.UtcNow)
            : RespuestaSugerida.DesdeMacro(conversacion.Id, origen, Guid.NewGuid(), TextoSugerido, Guid.NewGuid(), DateTime.UtcNow);
        sugerencia.Insertar();
        sugerencia.Confirmar(Guid.NewGuid(), revisadaEnUtc ?? DateTime.UtcNow);
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

    private Conversacion HiloDeCorreo(ConexionIntegracion conexion)
    {
        var conversacion = new Conversacion("Documentación", clienteId: Guid.NewGuid());
        conversacion.AsociarConexion(conexion.Id, "hilo-" + Guid.NewGuid());
        _comunicaciones.ListaMensajes.Add(conversacion.AgregarMensaje(
            DireccionMensaje.Entrante, CanalConversacion.Correo, Ana, "<p>Adjunto</p>", DateTime.UtcNow.AddMinutes(-10), "msg-1"));
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

    private Task<Result> Responder(Conversacion conversacion, string cuerpo) =>
        new ResponderConversacionCommandHandler(
                _conversaciones, _comunicaciones, _conexiones, new AlcanceDatosServiceFalso(), _graph,
                new AccesoGraphService(_credenciales, _graph), new FileStorageServiceFalso(),
                Options.Create(new ComunicacionesRemitenteOptions()), _unidad, NullLogger<ResponderConversacionCommandHandler>.Instance,
                _registro, new DescarteCambiosPendientesFalso())
            .Handle(new ResponderConversacionCommand(conversacion.Id, cuerpo), CancellationToken.None);

    private Task<Result> ResponderPorWhatsApp(Conversacion conversacion, string texto) =>
        new ResponderConversacionWhatsAppCommandHandler(
                _conversaciones, _comunicaciones, _conexiones, _lineas, new AlcanceDatosServiceFalso(), _whatsApp, _unidad,
                NullLogger<ResponderConversacionWhatsAppCommandHandler>.Instance, _registro, new DescarteCambiosPendientesFalso())
            .Handle(new ResponderConversacionWhatsAppCommand(conversacion.Id, texto), CancellationToken.None);

    private Task<Result> MigrarACorreo(Conversacion conversacion, string cuerpo) =>
        new MigrarConversacionACorreoCommandHandler(
                _conversaciones, _comunicaciones, _integraciones, _conexiones, new AlcanceDatosServiceFalso(), _graph,
                new AccesoGraphService(_credenciales, _graph), new ResolucionParticipanteConversacionServiceFalso(), _unidad, _registro)
            .Handle(new MigrarConversacionACorreoCommand(conversacion.Id, Ana, cuerpo), CancellationToken.None);

    private Task<Result<Guid>> EnviarNuevo(ConexionIntegracion conexion, string cuerpo) =>
        new EnviarMensajeNuevoCommandHandler(
                _conexiones, _conversaciones, _comunicaciones, new AlcanceDatosServiceFalso(), new InvalidadorAlcanceInerte(), _graph,
                new AccesoGraphService(_credenciales, _graph), new FileStorageServiceFalso(),
                new ResolucionParticipanteConversacionServiceFalso(), _unidad, _registro)
            .Handle(new EnviarMensajeNuevoCommand(conexion.Id, [Ana], "Documentación", cuerpo), CancellationToken.None);

    [Fact]
    public async Task Responder_tras_una_sugerencia_de_Vega_revisada_pide_la_cabecera_y_guarda_VegaRevisada()
    {
        var conversacion = HiloDeCorreo(Buzon());
        var sugerencia = Revisada(conversacion);

        var resultado = await Responder(conversacion, TextoSugerido);

        resultado.EsExitoso.Should().BeTrue();
        _graph.UltimaMarcaIaRevisada.Should().BeTrue();
        var mensaje = UltimoSaliente(conversacion);
        mensaje.OrigenContenido.Should().Be(OrigenContenidoMensaje.VegaRevisada);
        mensaje.RespuestaSugeridaId.Should().Be(sugerencia.Id);
        // Sin texto visible añadido: el cuerpo que sale es el que revisó la persona.
        _graph.UltimoCuerpoRespondido.Should().Be(TextoSugerido);
    }

    /// <summary>La regla no compara textos: una propuesta de Vega muy editada sigue partiendo de Vega.</summary>
    [Fact]
    public async Task Un_texto_de_Vega_muy_editado_sigue_marcado_como_de_IA()
    {
        var conversacion = HiloDeCorreo(Buzon());
        Revisada(conversacion);

        await Responder(conversacion, "<p>Hola Ana, te escribo yo con otras palabras y bastante más contexto del caso.</p>");

        _graph.UltimaMarcaIaRevisada.Should().BeTrue();
        UltimoSaliente(conversacion).OrigenContenido.Should().Be(OrigenContenidoMensaje.VegaRevisada);
    }

    [Fact]
    public async Task Responder_tras_una_macro_revisada_guarda_MacroRevisada_sin_cabecera()
    {
        var conversacion = HiloDeCorreo(Buzon());
        var sugerencia = Revisada(conversacion, deVega: false);

        await Responder(conversacion, TextoSugerido);

        _graph.UltimaMarcaIaRevisada.Should().BeFalse();
        UltimoSaliente(conversacion).OrigenContenido.Should().Be(OrigenContenidoMensaje.MacroRevisada);
        UltimoSaliente(conversacion).RespuestaSugeridaId.Should().Be(sugerencia.Id);
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

    [Fact]
    public async Task Una_sugerencia_que_ya_origino_un_mensaje_no_marca_el_siguiente()
    {
        var conversacion = HiloDeCorreo(Buzon());
        Revisada(conversacion);
        await Responder(conversacion, TextoSugerido);
        _comunicaciones.ListaMensajes.Add(UltimoSaliente(conversacion));

        await Responder(conversacion, "<p>Y una cosa más.</p>");

        _graph.UltimaMarcaIaRevisada.Should().BeFalse();
        UltimoSaliente(conversacion).OrigenContenido.Should().Be(OrigenContenidoMensaje.Persona);
    }

    [Fact]
    public async Task Una_sugerencia_revisada_de_un_entrante_anterior_no_marca_la_respuesta_al_nuevo()
    {
        var conversacion = HiloDeCorreo(Buzon());
        Revisada(conversacion);
        _comunicaciones.ListaMensajes.Add(conversacion.AgregarMensaje(
            DireccionMensaje.Entrante, CanalConversacion.Correo, Ana, "<p>Otra cosa</p>", DateTime.UtcNow, "msg-2"));

        await Responder(conversacion, "<p>Te contesto yo.</p>");

        _graph.UltimaMarcaIaRevisada.Should().BeFalse();
        UltimoSaliente(conversacion).OrigenContenido.Should().Be(OrigenContenidoMensaje.Persona);
    }

    [Fact]
    public async Task Con_dos_revisadas_del_mismo_entrante_vale_la_revisada_mas_reciente()
    {
        var conversacion = HiloDeCorreo(Buzon());
        Revisada(conversacion, deVega: false, revisadaEnUtc: DateTime.UtcNow.AddMinutes(-5));
        var reciente = Revisada(conversacion, revisadaEnUtc: DateTime.UtcNow);

        await Responder(conversacion, TextoSugerido);

        UltimoSaliente(conversacion).RespuestaSugeridaId.Should().Be(reciente.Id);
        _graph.UltimaMarcaIaRevisada.Should().BeTrue();
    }

    [Fact]
    public async Task Un_envio_que_falla_en_Graph_no_registra_mensaje()
    {
        var conversacion = HiloDeCorreo(Buzon());
        Revisada(conversacion);
        _graph.FallaEnvio = true;

        var resultado = await Responder(conversacion, TextoSugerido);

        resultado.EsFallido.Should().BeTrue();
        conversacion.Mensajes.Should().NotContain(m => m.Direccion == DireccionMensaje.Saliente);
    }

    [Fact]
    public async Task Por_WhatsApp_el_origen_queda_en_el_mensaje()
    {
        var conversacion = HiloDeWhatsApp(DateTime.UtcNow.AddHours(-1));
        var sugerencia = Revisada(conversacion);

        var resultado = await ResponderPorWhatsApp(conversacion, "Ya hemos recibido su documentación.");

        resultado.EsExitoso.Should().BeTrue();
        UltimoSaliente(conversacion).OrigenContenido.Should().Be(OrigenContenidoMensaje.VegaRevisada);
        UltimoSaliente(conversacion).RespuestaSugeridaId.Should().Be(sugerencia.Id);
    }

    [Fact]
    public async Task Al_migrar_a_correo_la_sugerencia_de_Vega_lleva_la_cabecera()
    {
        Buzon();
        var conversacion = HiloDeWhatsApp(DateTime.UtcNow.AddDays(-2));
        var sugerencia = Revisada(conversacion);

        var resultado = await MigrarACorreo(conversacion, TextoSugerido);

        resultado.EsExitoso.Should().BeTrue();
        _graph.UltimaMarcaIaRevisada.Should().BeTrue();
        var mensaje = conversacion.Mensajes.Last(m => m.Canal == CanalConversacion.Correo);
        mensaje.OrigenContenido.Should().Be(OrigenContenidoMensaje.VegaRevisada);
        mensaje.RespuestaSugeridaId.Should().Be(sugerencia.Id);
    }

    [Fact]
    public async Task Un_mensaje_nuevo_es_siempre_Persona_sin_cabecera()
    {
        var conexion = Buzon();

        var resultado = await EnviarNuevo(conexion, TextoSugerido);

        resultado.EsExitoso.Should().BeTrue();
        _graph.UltimaMarcaIaRevisada.Should().BeFalse();
        var mensaje = _conversaciones.Conversaciones.SelectMany(c => c.Mensajes).Single(m => m.Direccion == DireccionMensaje.Saliente);
        mensaje.OrigenContenido.Should().Be(OrigenContenidoMensaje.Persona);
    }
}
