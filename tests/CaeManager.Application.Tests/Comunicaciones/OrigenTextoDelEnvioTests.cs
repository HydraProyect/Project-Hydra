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
/// Cada comando que envía texto de una persona guarda en el Mensaje saliente el origen de ese texto, y lo fija el servidor
/// desde la sugerencia persistida y revisada. Los cuatro comandos se prueban con una sugerencia de Vega, con una de macro y
/// sin ninguna: el origen que se guarda es lo que permite demostrar después qué contenido partió de la IA.
/// </summary>
public class OrigenTextoDelEnvioTests
{
    private const string TextoSugerido = "<p>Estimada Ana:</p><p>Ya hemos recibido su documentación.</p>";
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

    public enum Via
    {
        ResponderPorCorreo,
        ResponderPorWhatsApp,
        MigrarACorreo,
        MensajeNuevo
    }

    /// <summary>El hilo en el que viven las sugerencias y la acción que envía desde él, devolviendo el Mensaje creado.</summary>
    private sealed record Escenario(Conversacion Hilo, Func<string, Task<Mensaje>> Enviar);

    private ConexionIntegracion Buzon()
    {
        var conexion = new ConexionIntegracion("cae@empresa.test", "Buzón CAE", clienteId: null);
        _conexiones.Agregar(conexion);
        _integraciones.ConexionesLista.Add(conexion);
        _credenciales.Agregar(new CredencialIntegracion(conexion.Id, "refresh-token"));
        return conexion;
    }

    private Conversacion Registrar(Conversacion conversacion)
    {
        _conversaciones.Agregar(conversacion);
        _comunicaciones.ListaConversaciones.Add(conversacion);
        _comunicaciones.ListaMensajes.AddRange(conversacion.Mensajes);
        return conversacion;
    }

    private Conversacion HiloDeCorreo(ConexionIntegracion conexion)
    {
        var conversacion = new Conversacion("Documentación", clienteId: Guid.NewGuid());
        conversacion.AsociarConexion(conexion.Id, "hilo-" + Guid.NewGuid());
        conversacion.AgregarMensaje(
            DireccionMensaje.Entrante, CanalConversacion.Correo, "ana@contrata.test", "<p>Adjunto</p>", mensajeExternoId: "msg-" + Guid.NewGuid());
        return Registrar(conversacion);
    }

    private Conversacion HiloDeWhatsApp(DateTime ultimoEntranteUtc)
    {
        var linea = new ConexionIntegracion("+34686543364", "Línea", proveedor: ProveedorIntegracion.WhatsApp);
        _conexiones.Agregar(linea);
        _lineas.Agregar(new LineaWhatsApp(
            linea.Id, "111222333", "waba-1", "+34686543364", "token", ModoAsignacionLinea.GestorFijo, Guid.NewGuid()));

        var conversacion = Conversacion.CrearWhatsApp(Telefono, linea.Id, null, Guid.NewGuid());
        conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.WhatsApp, Telefono, "Hola", ultimoEntranteUtc, "wamid.in");
        return Registrar(conversacion);
    }

    private static Mensaje UltimoSaliente(Conversacion conversacion) =>
        conversacion.Mensajes.Single(m => m.Direccion == DireccionMensaje.Saliente);

    private Escenario Preparar(Via via)
    {
        switch (via)
        {
            case Via.ResponderPorCorreo:
                var hiloCorreo = HiloDeCorreo(Buzon());
                return new Escenario(hiloCorreo, async cuerpo =>
                {
                    (await new ResponderConversacionCommandHandler(
                            _conversaciones, _comunicaciones, _conexiones, new AlcanceDatosServiceFalso(), _graph,
                            new AccesoGraphService(_credenciales, _graph), new FileStorageServiceFalso(),
                            Options.Create(new ComunicacionesRemitenteOptions()), _unidad,
                            NullLogger<ResponderConversacionCommandHandler>.Instance, _registro, new DescarteCambiosPendientesFalso())
                        .Handle(new ResponderConversacionCommand(hiloCorreo.Id, cuerpo), CancellationToken.None)).EsExitoso.Should().BeTrue();
                    return UltimoSaliente(hiloCorreo);
                });

            case Via.ResponderPorWhatsApp:
                var hiloWhatsApp = HiloDeWhatsApp(DateTime.UtcNow.AddHours(-1));
                return new Escenario(hiloWhatsApp, async texto =>
                {
                    (await new ResponderConversacionWhatsAppCommandHandler(
                            _conversaciones, _comunicaciones, _conexiones, _lineas, new AlcanceDatosServiceFalso(), _whatsApp, _unidad,
                            NullLogger<ResponderConversacionWhatsAppCommandHandler>.Instance, _registro, new DescarteCambiosPendientesFalso())
                        .Handle(new ResponderConversacionWhatsAppCommand(hiloWhatsApp.Id, texto), CancellationToken.None)).EsExitoso.Should().BeTrue();
                    return UltimoSaliente(hiloWhatsApp);
                });

            case Via.MigrarACorreo:
                Buzon();
                var hiloAMigrar = HiloDeWhatsApp(DateTime.UtcNow.AddHours(-30));
                return new Escenario(hiloAMigrar, async cuerpo =>
                {
                    (await new MigrarConversacionACorreoCommandHandler(
                            _conversaciones, _comunicaciones, _integraciones, _conexiones, new AlcanceDatosServiceFalso(), _graph,
                            new AccesoGraphService(_credenciales, _graph), new ResolucionParticipanteConversacionServiceFalso(), _unidad, _registro)
                        .Handle(new MigrarConversacionACorreoCommand(hiloAMigrar.Id, "ana@contrata.test", cuerpo), CancellationToken.None))
                        .EsExitoso.Should().BeTrue();
                    return UltimoSaliente(hiloAMigrar);
                });

            default:
                var conexion = Buzon();
                var hiloDelBuzon = HiloDeCorreo(conexion);
                return new Escenario(hiloDelBuzon, async cuerpo =>
                {
                    (await new EnviarMensajeNuevoCommandHandler(
                            _conexiones, _conversaciones, _comunicaciones, new AlcanceDatosServiceFalso(), _graph,
                            new AccesoGraphService(_credenciales, _graph), new FileStorageServiceFalso(),
                            new ResolucionParticipanteConversacionServiceFalso(), _unidad, _registro)
                        .Handle(new EnviarMensajeNuevoCommand(conexion.Id, ["ana@contrata.test"], "Documentación", cuerpo), CancellationToken.None))
                        .EsExitoso.Should().BeTrue();
                    return _conversaciones.Conversaciones.Single(c => c.Id != hiloDelBuzon.Id).Mensajes.Single();
                });
        }
    }

    private static Guid UltimoEntrante(Conversacion conversacion) =>
        conversacion.Mensajes.Where(m => m.Direccion == DireccionMensaje.Entrante).OrderBy(m => m.FechaUtc).Last().Id;

    private RespuestaSugerida Revisada(
        Conversacion conversacion, OrigenRespuestaSugerida origen, string texto = TextoSugerido, Guid? mensajeOrigenId = null,
        DateTime? revisadaEnUtc = null)
    {
        var origenId = mensajeOrigenId ?? UltimoEntrante(conversacion);
        var respuesta = origen == OrigenRespuestaSugerida.Vega
            ? RespuestaSugerida.DeVega(conversacion.Id, origenId, texto, Guid.NewGuid(), DateTime.UtcNow)
            : RespuestaSugerida.DesdeMacro(conversacion.Id, origenId, Guid.NewGuid(), texto, Guid.NewGuid(), DateTime.UtcNow);
        respuesta.Insertar();
        respuesta.Confirmar(Guid.NewGuid(), revisadaEnUtc ?? DateTime.UtcNow);
        _comunicaciones.ListaRespuestasSugeridas.Add(respuesta);
        return respuesta;
    }

    [Theory]
    [InlineData(Via.ResponderPorCorreo)]
    [InlineData(Via.ResponderPorWhatsApp)]
    [InlineData(Via.MigrarACorreo)]
    [InlineData(Via.MensajeNuevo)]
    public async Task Con_una_sugerencia_de_Vega_revisada_el_mensaje_queda_como_IaRevisada_y_la_nombra(Via via)
    {
        var escenario = Preparar(via);
        var sugerencia = Revisada(escenario.Hilo, OrigenRespuestaSugerida.Vega);

        var mensaje = await escenario.Enviar(TextoSugerido);

        mensaje.OrigenTexto.Should().Be(OrigenTextoMensaje.IaRevisada);
        mensaje.RespuestaSugeridaId.Should().Be(sugerencia.Id);
    }

    [Theory]
    [InlineData(Via.ResponderPorCorreo)]
    [InlineData(Via.ResponderPorWhatsApp)]
    [InlineData(Via.MigrarACorreo)]
    [InlineData(Via.MensajeNuevo)]
    public async Task Con_una_macro_revisada_el_mensaje_queda_como_MacroRevisada_y_la_nombra(Via via)
    {
        var escenario = Preparar(via);
        var sugerencia = Revisada(escenario.Hilo, OrigenRespuestaSugerida.Macro);

        var mensaje = await escenario.Enviar(TextoSugerido);

        mensaje.OrigenTexto.Should().Be(OrigenTextoMensaje.MacroRevisada);
        mensaje.RespuestaSugeridaId.Should().Be(sugerencia.Id);
    }

    [Theory]
    [InlineData(Via.ResponderPorCorreo)]
    [InlineData(Via.ResponderPorWhatsApp)]
    [InlineData(Via.MigrarACorreo)]
    [InlineData(Via.MensajeNuevo)]
    public async Task Sin_sugerencia_el_mensaje_queda_como_Persona_sin_sugerencia(Via via)
    {
        var escenario = Preparar(via);

        var mensaje = await escenario.Enviar(EscritoAMano);

        mensaje.OrigenTexto.Should().Be(OrigenTextoMensaje.Persona);
        mensaje.RespuestaSugeridaId.Should().BeNull();
    }

    [Theory]
    [InlineData(Via.ResponderPorCorreo)]
    [InlineData(Via.ResponderPorWhatsApp)]
    [InlineData(Via.MigrarACorreo)]
    [InlineData(Via.MensajeNuevo)]
    public async Task Una_sugerencia_revisada_cuyo_texto_no_se_envia_deja_el_mensaje_como_Persona(Via via)
    {
        var escenario = Preparar(via);
        Revisada(escenario.Hilo, OrigenRespuestaSugerida.Vega);

        var mensaje = await escenario.Enviar(EscritoAMano);

        mensaje.OrigenTexto.Should().Be(OrigenTextoMensaje.Persona);
        mensaje.RespuestaSugeridaId.Should().BeNull();
    }

    /// <summary>Añadir un saludo o una firma a la sugerencia revisada no le quita el origen: el bloqueo tampoco se elude así.</summary>
    [Fact]
    public async Task Una_sugerencia_revisada_con_una_firma_añadida_sigue_siendo_IaRevisada()
    {
        var escenario = Preparar(Via.ResponderPorCorreo);
        var sugerencia = Revisada(escenario.Hilo, OrigenRespuestaSugerida.Vega);

        var mensaje = await escenario.Enviar("<p>Buenos días.</p>" + TextoSugerido + "<p>Un saludo, Marta</p>");

        mensaje.OrigenTexto.Should().Be(OrigenTextoMensaje.IaRevisada);
        mensaje.RespuestaSugeridaId.Should().Be(sugerencia.Id);
    }

    [Fact]
    public async Task Con_dos_revisadas_que_coinciden_gana_la_de_texto_identico()
    {
        var escenario = Preparar(Via.ResponderPorCorreo);
        Revisada(escenario.Hilo, OrigenRespuestaSugerida.Macro, "<p>Ya hemos recibido su documentación.</p>");
        var identica = Revisada(escenario.Hilo, OrigenRespuestaSugerida.Vega, TextoSugerido);

        var mensaje = await escenario.Enviar(TextoSugerido);

        mensaje.OrigenTexto.Should().Be(OrigenTextoMensaje.IaRevisada);
        mensaje.RespuestaSugeridaId.Should().Be(identica.Id);
    }

    [Fact]
    public async Task Con_dos_revisadas_de_texto_identico_gana_la_revisada_mas_tarde()
    {
        var escenario = Preparar(Via.ResponderPorCorreo);
        var ahora = DateTime.UtcNow;
        Revisada(escenario.Hilo, OrigenRespuestaSugerida.Macro, revisadaEnUtc: ahora.AddMinutes(-10));
        var reciente = Revisada(escenario.Hilo, OrigenRespuestaSugerida.Vega, revisadaEnUtc: ahora);

        var mensaje = await escenario.Enviar(TextoSugerido);

        mensaje.RespuestaSugeridaId.Should().Be(reciente.Id);
        mensaje.OrigenTexto.Should().Be(OrigenTextoMensaje.IaRevisada);
    }

    /// <summary>Límite declarado: una sugerencia contesta al último entrante, y la de uno anterior no cuenta, como en el bloqueo.</summary>
    [Fact]
    public async Task La_sugerencia_revisada_para_un_entrante_anterior_no_da_origen()
    {
        var escenario = Preparar(Via.ResponderPorCorreo);
        var anterior = UltimoEntrante(escenario.Hilo);
        var siguiente = escenario.Hilo.AgregarMensaje(
            DireccionMensaje.Entrante, CanalConversacion.Correo, "ana@contrata.test", "<p>Otra cosa</p>", DateTime.UtcNow.AddMinutes(1),
            "msg-siguiente");
        _comunicaciones.ListaMensajes.Add(siguiente);
        Revisada(escenario.Hilo, OrigenRespuestaSugerida.Vega, mensajeOrigenId: anterior);

        var mensaje = await escenario.Enviar(TextoSugerido);

        mensaje.OrigenTexto.Should().Be(OrigenTextoMensaje.Persona);
        mensaje.RespuestaSugeridaId.Should().BeNull();
    }

    /// <summary>
    /// Se envía el texto exacto de la sugerencia descartada, de un entrante anterior (el del último no sale, lo impide el
    /// bloqueo): si el origen mirase también las descartadas, este mensaje saldría marcado.
    /// </summary>
    [Fact]
    public async Task Una_sugerencia_descartada_nunca_da_origen_aunque_se_envie_su_texto_exacto()
    {
        var escenario = Preparar(Via.ResponderPorCorreo);
        var anterior = UltimoEntrante(escenario.Hilo);
        var siguiente = escenario.Hilo.AgregarMensaje(
            DireccionMensaje.Entrante, CanalConversacion.Correo, "ana@contrata.test", "<p>Otra cosa</p>", DateTime.UtcNow.AddMinutes(1),
            "msg-siguiente");
        _comunicaciones.ListaMensajes.Add(siguiente);
        var descartada = RespuestaSugerida.DeVega(escenario.Hilo.Id, anterior, TextoSugerido, Guid.NewGuid(), DateTime.UtcNow);
        descartada.Descartar();
        _comunicaciones.ListaRespuestasSugeridas.Add(descartada);

        var mensaje = await escenario.Enviar(TextoSugerido);

        mensaje.OrigenTexto.Should().Be(OrigenTextoMensaje.Persona);
        mensaje.RespuestaSugeridaId.Should().BeNull();
    }

    /// <summary>
    /// Dos candidatas contenidas en el cuerpo, ninguna idéntica: gana la de texto más largo aunque la corta se revisara más
    /// tarde, de modo que el desempate por fecha no puede explicar el resultado.
    /// </summary>
    [Fact]
    public async Task Con_dos_contenidas_gana_la_de_texto_mas_largo_aunque_la_corta_se_revisara_mas_tarde()
    {
        var escenario = Preparar(Via.ResponderPorCorreo);
        var ahora = DateTime.UtcNow;
        var larga = Revisada(escenario.Hilo, OrigenRespuestaSugerida.Vega, TextoSugerido, revisadaEnUtc: ahora.AddMinutes(-10));
        Revisada(escenario.Hilo, OrigenRespuestaSugerida.Macro, "<p>Ya hemos recibido su documentación.</p>", revisadaEnUtc: ahora);

        var mensaje = await escenario.Enviar("<p>Buenos días.</p>" + TextoSugerido + "<p>Un saludo, Marta</p>");

        mensaje.OrigenTexto.Should().Be(OrigenTextoMensaje.IaRevisada);
        mensaje.RespuestaSugeridaId.Should().Be(larga.Id);
    }

    /// <summary>Entre dos hilos visibles del buzón con el mismo texto revisado, la sugerencia revisada más tarde da el origen.</summary>
    [Fact]
    public async Task Por_buzon_gana_la_revisada_mas_tarde_entre_dos_hilos_visibles()
    {
        var conexion = Buzon();
        var ahora = DateTime.UtcNow;
        Revisada(HiloDeCorreo(conexion), OrigenRespuestaSugerida.Vega, revisadaEnUtc: ahora.AddMinutes(-10));
        var reciente = Revisada(HiloDeCorreo(conexion), OrigenRespuestaSugerida.Macro, revisadaEnUtc: ahora);

        var resultado = await _comunicaciones.ComprobarEnvioPorBuzonAsync(
            new AlcanceDatosServiceFalso(), conexion.Id, TextoSugerido, CancellationToken.None);

        resultado.Valor.Should().Be(new OrigenTextoEnvio(OrigenTextoMensaje.MacroRevisada, reciente.Id));
    }
    [Fact]
    public async Task El_origen_por_buzon_ignora_una_sugerencia_revisada_de_un_hilo_que_quien_envia_no_ve()
    {
        var conexion = Buzon();
        var hilo = HiloDeCorreo(conexion);
        Revisada(hilo, OrigenRespuestaSugerida.Vega);

        var sinVer = await _comunicaciones.ComprobarEnvioPorBuzonAsync(
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false), conexion.Id, TextoSugerido, CancellationToken.None);
        var viendolo = await _comunicaciones.ComprobarEnvioPorBuzonAsync(
            new AlcanceDatosServiceFalso(), conexion.Id, TextoSugerido, CancellationToken.None);

        sinVer.Valor.Should().Be(OrigenTextoEnvio.DePersona);
        viendolo.Valor.Origen.Should().Be(OrigenTextoMensaje.IaRevisada);
    }

    [Fact]
    public async Task El_origen_por_buzon_ignora_las_sugerencias_de_otro_buzon()
    {
        var conexion = Buzon();
        Revisada(HiloDeCorreo(Buzon()), OrigenRespuestaSugerida.Vega);

        var resultado = await _comunicaciones.ComprobarEnvioPorBuzonAsync(
            new AlcanceDatosServiceFalso(), conexion.Id, TextoSugerido, CancellationToken.None);

        resultado.Valor.Should().Be(OrigenTextoEnvio.DePersona);
    }

    [Fact]
    public void Un_origen_de_macro_o_de_Vega_se_traduce_al_origen_del_texto()
    {
        var id = Guid.NewGuid();

        OrigenTextoEnvio.DeSugerencia(id, OrigenRespuestaSugerida.Vega).Should().Be(new OrigenTextoEnvio(OrigenTextoMensaje.IaRevisada, id));
        OrigenTextoEnvio.DeSugerencia(id, OrigenRespuestaSugerida.Macro).Should().Be(new OrigenTextoEnvio(OrigenTextoMensaje.MacroRevisada, id));
        OrigenTextoEnvio.DePersona.Should().Be(new OrigenTextoEnvio(OrigenTextoMensaje.Persona, null));
    }

    [Fact]
    public void Un_origen_de_sugerencia_no_contemplado_lanza_en_vez_de_caer_en_otro_origen()
    {
        var traducir = () => OrigenTextoEnvio.DeSugerencia(Guid.NewGuid(), (OrigenRespuestaSugerida)99);

        traducir.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("origen");
    }
}