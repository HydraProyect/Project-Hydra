using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Comunicaciones.Commands.EnviarMensajeNuevo;
using CaeManager.Application.Comunicaciones.Commands.ResponderConversacion;
using CaeManager.Application.Comunicaciones.Commands.ResponderConversacionWhatsApp;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Application.Tests.Comunicaciones;

/// <summary>C-11, decisión D-5 (a): cada correo que sale deja una fila, con el destinatario enmascarado y antes de salir.</summary>
public class RastroSalidaComunicacionesTests
{
    private readonly ConexionIntegracion _conexion = new("cae@cliente.com", "Buzón CAE");
    private readonly Microsoft365GraphClientFalso _graph = new();
    private readonly ConversacionRepositorioFalso _conversaciones = new();
    private readonly DescarteCambiosPendientesFalso _descarte = new();
    private readonly WhatsAppCloudApiClientFalso _whatsApp = new();

    private (ConexionIntegracionRepositorioFalso Conexiones, AccesoGraphService Acceso) Dependencias()
    {
        var conexiones = new ConexionIntegracionRepositorioFalso();
        conexiones.Agregar(_conexion);
        var credenciales = new CredencialIntegracionRepositorioFalso();
        credenciales.Agregar(new CredencialIntegracion(_conexion.Id, "refresh-token"));
        return (conexiones, new AccesoGraphService(credenciales, _graph));
    }

    private EnviarMensajeNuevoCommandHandler HandlerNuevo(RegistroExportacionFalso registro)
    {
        var (conexiones, acceso) = Dependencias();
        return new EnviarMensajeNuevoCommandHandler(
            conexiones, _conversaciones, new AlcanceDatosServiceFalso(), _graph, acceso,
            new FileStorageServiceFalso(), new ResolucionParticipanteConversacionServiceFalso(), new UnitOfWorkFalso(), registro);
    }

    private ResponderConversacionCommandHandler HandlerRespuesta(
        RegistroExportacionFalso registro, bool remitenteSimulado = false, IUnitOfWork? unitOfWork = null)
    {
        var (conexiones, acceso) = Dependencias();
        return new ResponderConversacionCommandHandler(
            _conversaciones, conexiones, new AlcanceDatosServiceFalso(), _graph, acceso, new FileStorageServiceFalso(),
            Options.Create(new ComunicacionesRemitenteOptions { PermitirRemitenteSimulado = remitenteSimulado }),
            unitOfWork ?? new UnitOfWorkFalso(), NullLogger<ResponderConversacionCommandHandler>.Instance, registro, _descarte);
    }

    private Conversacion ConversacionConEntrante(bool conBuzon = true)
    {
        var conversacion = new Conversacion("Vigencias", clienteId: Guid.NewGuid());
        if (conBuzon)
            conversacion.AsociarConexion(_conexion.Id, "hilo-1");
        conversacion.AgregarMensaje(
            DireccionMensaje.Entrante, CanalConversacion.Correo, "contraparte@ejemplo.com", "<p>hola</p>", mensajeExternoId: "graph-msg-1");
        _conversaciones.Agregar(conversacion);
        return conversacion;
    }

    private EnviarMensajeNuevoCommand Comando(OrigenSalidaComunicacion origen = OrigenSalidaComunicacion.Redaccion) => new(
        _conexion.Id, ["contacto@cliente.com", "otra@tercero.es"], "Asunto con datos", "<p>Cuerpo con datos</p>",
        Adjuntos: [new AdjuntoParaEnviarDto("Juan Perez 12345678Z.pdf", "application/pdf", new byte[10])],
        Copia: ["copia@cliente.com"], Origen: origen);

    [Fact]
    public async Task Un_mensaje_nuevo_deja_una_sola_fila_con_destinatarios_enmascarados()
    {
        var registro = new RegistroExportacionFalso();

        await HandlerNuevo(registro).Handle(Comando(OrigenSalidaComunicacion.Informe), CancellationToken.None);

        var fila = registro.Filas.Should().ContainSingle().Subject;
        fila.EntidadTipo.Should().Be(nameof(Mensaje));
        fila.Filas.Should().Be(1, "cuentan los adjuntos, no los destinatarios");
        fila.Criterios["origen"].Should().Be("Informe");
        fila.Criterios["conexionId"].Should().Be(_conexion.Id.ToString());
        fila.Criterios["destinatarios"].Should().Be("c***@cliente.com, o***@tercero.es");
        fila.Criterios["copia"].Should().Be("c***@cliente.com");
        fila.Criterios["adjuntosBytes"].Should().Be("10");
    }

    [Fact]
    public async Task La_fila_nunca_lleva_direcciones_completas_asunto_cuerpo_ni_nombre_de_adjunto()
    {
        var registro = new RegistroExportacionFalso();

        await HandlerNuevo(registro).Handle(Comando(), CancellationToken.None);

        var texto = string.Join("|", registro.Filas.Single().Criterios.SelectMany(c => new[] { c.Key, c.Value }));
        texto.Should().NotContainAny("contacto@", "otra@", "copia@", "Asunto", "Cuerpo", "Juan", "12345678Z");
    }

    [Fact]
    public async Task La_fila_se_escribe_antes_de_refrescar_el_token_y_de_enviar()
    {
        // El refresco deja la credencial modificada en el contexto, y el registro real se niega a guardar con cambios
        // pendientes ajenos: si la fila fuera después del token, todo envío con token caducado fallaría.
        int? refrescosAlRegistrar = null, enviosAlRegistrar = null;
        var registro = new RegistroExportacionFalso
        {
            AlRegistrar = () => (refrescosAlRegistrar, enviosAlRegistrar) = (_graph.VecesRefrescado, _graph.VecesEnviado),
        };

        await HandlerNuevo(registro).Handle(Comando(), CancellationToken.None);

        _graph.VecesEnviado.Should().Be(1);
        _graph.VecesRefrescado.Should().Be(1, "sin refresco este test no probaría el orden respecto al token");
        refrescosAlRegistrar.Should().Be(0);
        enviosAlRegistrar.Should().Be(0);
    }

    [Fact]
    public async Task Si_la_fila_no_se_puede_guardar_el_mensaje_nuevo_no_sale()
    {
        var acto = () => HandlerNuevo(new RegistroExportacionFalso { Falla = true }).Handle(Comando(), CancellationToken.None);

        await acto.Should().ThrowAsync<InvalidOperationException>();

        _graph.VecesEnviado.Should().Be(0);
        _conversaciones.Conversaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_autorizacion_de_buzon_no_queda_fila_de_salida()
    {
        _conexion.Deshabilitar();
        var registro = new RegistroExportacionFalso();

        var resultado = await HandlerNuevo(registro).Handle(Comando(), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        registro.Filas.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_respuesta_enmascara_a_quien_escribio_y_cuenta_sus_adjuntos()
    {
        var conversacion = ConversacionConEntrante();
        var registro = new RegistroExportacionFalso();

        var resultado = await HandlerRespuesta(registro).Handle(
            new ResponderConversacionCommand(
                conversacion.Id, "<p>respuesta</p>", [new AdjuntoParaEnviarDto("a.pdf", "application/pdf", new byte[4])]),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var fila = registro.Filas.Should().ContainSingle().Subject;
        fila.Filas.Should().Be(1);
        fila.Criterios["origen"].Should().Be("Respuesta");
        fila.Criterios["destinatarios"].Should().Be("c***@ejemplo.com");
    }

    [Fact]
    public async Task Si_la_fila_no_se_puede_guardar_la_respuesta_no_sale()
    {
        var conversacion = ConversacionConEntrante();

        var acto = () => HandlerRespuesta(new RegistroExportacionFalso { Falla = true }).Handle(
            new ResponderConversacionCommand(conversacion.Id, "<p>respuesta</p>"), CancellationToken.None);

        await acto.Should().ThrowAsync<InvalidOperationException>();
        _graph.VecesEnviado.Should().Be(0);
    }

    [Fact]
    public async Task El_remitente_simulado_de_las_demos_no_deja_fila_porque_nada_sale()
    {
        var conversacion = ConversacionConEntrante(conBuzon: false);
        var registro = new RegistroExportacionFalso();

        var resultado = await HandlerRespuesta(registro, remitenteSimulado: true).Handle(
            new ResponderConversacionCommand(conversacion.Id, "<p>respuesta</p>"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        registro.Filas.Should().BeEmpty();
    }

    [Fact]
    public async Task Si_la_respuesta_sale_y_no_se_guarda_descarta_los_cambios_para_no_bloquear_el_siguiente_envio()
    {
        var conversacion = ConversacionConEntrante();
        var unitOfWork = new UnitOfWorkFalso { ExcepcionAlGuardar = new InvalidOperationException("Fallo simulado de guardado.") };

        var resultado = await HandlerRespuesta(new RegistroExportacionFalso(), unitOfWork: unitOfWork).Handle(
            new ResponderConversacionCommand(conversacion.Id, "<p>respuesta</p>"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        _descarte.VecesDescartado.Should().Be(1);
    }

    private (ResponderConversacionWhatsAppCommandHandler Handler, Conversacion Conversacion) WhatsApp(
        RegistroExportacionFalso registro, UnitOfWorkFalso? unitOfWork = null)
    {
        var conversacion = Conversacion.CrearWhatsApp("+34600111222", _conexion.Id, Guid.NewGuid(), null);
        conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.WhatsApp, "+34600111222", "hola");
        _conversaciones.Agregar(conversacion);
        var (conexiones, _) = Dependencias();
        var lineas = new LineaWhatsAppRepositorioFalso();
        lineas.Agregar(new LineaWhatsApp(_conexion.Id, "111222333", "waba", "+34900000000", "token", ModoAsignacionLinea.GestorFijo, Guid.NewGuid()));
        var handler = new ResponderConversacionWhatsAppCommandHandler(
            _conversaciones, conexiones, lineas, new AlcanceDatosServiceFalso(), _whatsApp, unitOfWork ?? new UnitOfWorkFalso(),
            NullLogger<ResponderConversacionWhatsAppCommandHandler>.Instance, registro, _descarte);
        return (handler, conversacion);
    }

    [Fact]
    public async Task Una_respuesta_por_WhatsApp_deja_fila_sin_el_telefono()
    {
        var registro = new RegistroExportacionFalso
        {
            AlRegistrar = () => _whatsApp.EnviosRealizados.Should().BeEmpty("la fila se escribe antes de enviar"),
        };
        var (handler, conversacion) = WhatsApp(registro);

        await handler.Handle(new ResponderConversacionWhatsAppCommand(conversacion.Id, "respuesta"), CancellationToken.None);

        _whatsApp.EnviosRealizados.Should().ContainSingle();
        var fila = registro.Filas.Should().ContainSingle().Subject;
        fila.Criterios["origen"].Should().Be("RespuestaWhatsApp");
        fila.Criterios["destinatarios"].Should().Be("***");
        fila.Criterios.Values.Should().NotContain(valor => valor.Contains("600111222"));
    }

    [Fact]
    public async Task Sin_fila_no_sale_el_WhatsApp()
    {
        var (handler, conversacion) = WhatsApp(new RegistroExportacionFalso { Falla = true });

        var acto = () => handler.Handle(new ResponderConversacionWhatsAppCommand(conversacion.Id, "respuesta"), CancellationToken.None);

        await acto.Should().ThrowAsync<InvalidOperationException>();
        _whatsApp.EnviosRealizados.Should().BeEmpty();
    }

    [Fact]
    public async Task Si_el_WhatsApp_sale_y_no_se_guarda_descarta_los_cambios()
    {
        var unitOfWork = new UnitOfWorkFalso { ExcepcionAlGuardar = new InvalidOperationException("Fallo simulado de guardado.") };
        var (handler, conversacion) = WhatsApp(new RegistroExportacionFalso(), unitOfWork);

        var resultado = await handler.Handle(new ResponderConversacionWhatsAppCommand(conversacion.Id, "respuesta"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        _descarte.VecesDescartado.Should().Be(1);
    }

    [Theory]
    [InlineData("contacto@cliente.com", "c***@cliente.com")]
    [InlineData("  a@b.es ", "a***@b.es")]
    [InlineData("sin-arroba", "***")]
    [InlineData("@solo-dominio.com", "***")]
    [InlineData("solo-local@", "***")]
    [InlineData("", "***")]
    public void Enmascarar_conserva_solo_la_inicial_y_el_dominio(string correo, string esperado) =>
        RegistroSalidaComunicaciones.Enmascarar(correo).Should().Be(esperado);
}
