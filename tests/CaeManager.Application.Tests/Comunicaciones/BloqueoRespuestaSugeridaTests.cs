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

    private RespuestaSugerida Sugerencia(Conversacion conversacion, EstadoRespuestaSugerida estado, string texto = TextoSugerido)
    {
        var respuesta = RespuestaSugerida.DeVega(conversacion.Id, Guid.NewGuid(), texto, Guid.NewGuid(), DateTime.UtcNow);
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
                Options.Create(new ComunicacionesRemitenteOptions()), _unidad, NullLogger<ResponderConversacionCommandHandler>.Instance)
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
                NullLogger<ResponderConversacionWhatsAppCommandHandler>.Instance)
            .Handle(new ResponderConversacionWhatsAppCommand(conversacion.Id, texto), CancellationToken.None);

    private Task<Result> MigrarACorreo(Conversacion conversacion, string cuerpo) =>
        new MigrarConversacionACorreoCommandHandler(
                _conversaciones, _comunicaciones, _integraciones, _conexiones, new AlcanceDatosServiceFalso(), _graph,
                new AccesoGraphService(_credenciales, _graph), new ResolucionParticipanteConversacionServiceFalso(), _unidad)
            .Handle(new MigrarConversacionACorreoCommand(conversacion.Id, "ana@contrata.test", cuerpo), CancellationToken.None);

    private Task<Result<Guid>> EnviarNuevo(ConexionIntegracion conexion, string cuerpo) =>
        new EnviarMensajeNuevoCommandHandler(
                _conexiones, _conversaciones, _comunicaciones, new AlcanceDatosServiceFalso(), _graph,
                new AccesoGraphService(_credenciales, _graph), new FileStorageServiceFalso(),
                new ResolucionParticipanteConversacionServiceFalso(), _unidad)
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

    [Theory]
    [InlineData("<p>Hola</p>  <p>Ana</p>", "Hola Ana")]
    [InlineData("Hola\r\n\tAna", "Hola Ana")]
    [InlineData("a &lt; b &amp; c", "a < b & c")]
    [InlineData("<br/>", "")]
    public void Normaliza_sin_html_y_con_los_espacios_colapsados(string texto, string esperado) =>
        BloqueoRespuestaSugerida.Normalizar(texto).Should().Be(esperado);
}
