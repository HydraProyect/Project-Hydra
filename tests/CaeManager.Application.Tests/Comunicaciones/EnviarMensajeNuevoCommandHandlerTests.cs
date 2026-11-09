using CaeManager.Application.Comunicaciones.Commands.EnviarMensajeNuevo;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Comunicaciones;

public class EnviarMensajeNuevoCommandHandlerTests
{
    private static (EnviarMensajeNuevoCommandHandler Handler, ConversacionRepositorioFalso Conversaciones, Microsoft365GraphClientFalso GraphClient) CrearHandler(
        ConexionIntegracion conexion, bool noOperativaEnBase = false, Action<ConexionIntegracionRepositorioFalso>? configurarRepositorio = null)
    {
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        if (noOperativaEnBase)
            conexionRepositorio.NoOperativasEnBase.Add(conexion.Id);
        configurarRepositorio?.Invoke(conexionRepositorio);
        var credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        credencialRepositorio.Agregar(new CredencialIntegracion(conexion.Id, "refresh-token"));
        var graphClient = new Microsoft365GraphClientFalso();
        var accesoGraph = new AccesoGraphService(credencialRepositorio, graphClient);
        var conversaciones = new ConversacionRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();

        var handler = new EnviarMensajeNuevoCommandHandler(
            conexionRepositorio, conversaciones, new ComunicacionesQueryContextFalso(), new AlcanceDatosServiceFalso(), new CaeManager.Application.Common.InvalidadorAlcanceInerte(), graphClient, accesoGraph,
            new FileStorageServiceFalso(), new ResolucionParticipanteConversacionServiceFalso(), unitOfWork, new RegistroExportacionFalso());

        return (handler, conversaciones, graphClient);
    }

    [Fact]
    public async Task Crea_una_conversacion_nueva_con_el_mensaje_saliente()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE").Verificada();
        var (handler, conversaciones, _) = CrearHandler(conexion);

        var resultado = await handler.Handle(
            new EnviarMensajeNuevoCommand(conexion.Id, ["contacto@cliente.com"], "Bienvenida", "<p>Hola</p>"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var conversacion = conversaciones.Conversaciones.Should().ContainSingle().Which;
        conversacion.Id.Should().Be(resultado.Valor);
        conversacion.Asunto.Should().Be("Bienvenida");
        conversacion.ConexionIntegracionId.Should().Be(conexion.Id);
        conversacion.HiloExternoId.Should().Be("hilo-ext-falso-456");
        var mensaje = conversacion.Mensajes.Should().ContainSingle(m => m.Direccion == DireccionMensaje.Saliente && m.CuerpoHtml == "<p>Hola</p>").Which;
        mensaje.MensajeExternoId.Should().Be("msg-ext-falso-123");
        conversacion.Participantes.Should().ContainSingle(p => p.Email == "contacto@cliente.com" && p.Rol == RolParticipante.Para);
    }

    /// <summary>
    /// También cubre a quien elige el buzón por su cuenta (RegistroEnvioReclamacionService, PedirPrioridadValidacion):
    /// todos envían por aquí, y un buzón sin verificar falla con un error claro en vez de cambiar de canal en silencio.
    /// </summary>
    [Fact]
    public async Task Con_la_identidad_del_buzon_sin_verificar_no_envia_y_lo_dice()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");
        conexion.FijarIdentidadBuzon("inbox-cae");
        var (handler, conversaciones, _) = CrearHandler(conexion);

        var resultado = await handler.Handle(
            new EnviarMensajeNuevoCommand(conexion.Id, ["contacto@cliente.com"], "Bienvenida", "<p>Hola</p>"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(CaeManager.Application.Integraciones.ErroresBuzonSinVerificar.Codigo);
        conversaciones.Conversaciones.Should().BeEmpty();
    }

    /// <summary>
    /// Puerta justo antes de enviar: la instancia cargada dice operativa, pero otro proceso la pasó a ConError entre la
    /// carga y el envío. Cubre también Reclamación y PedirPrioridad, que envían por aquí.
    /// </summary>
    [Fact]
    public async Task Si_la_conexion_dejo_de_estar_operativa_en_la_base_antes_del_envio_no_envia()
    {
        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE").Verificada();
        var (handler, conversaciones, graphClient) = CrearHandler(conexion, noOperativaEnBase: true);

        var resultado = await handler.Handle(
            new EnviarMensajeNuevoCommand(conexion.Id, ["contacto@tenant.com"], "Bienvenida", "<p>Hola</p>"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(CaeManager.Application.Integraciones.ErroresBuzonSinVerificar.Codigo);
        graphClient.VecesEnviado.Should().Be(0);
        conversaciones.Conversaciones.Should().BeEmpty();
    }

    /// <summary>
    /// Crear el borrador y enviarlo son dos llamadas a Graph: la conexión pasa la puerta antes de crear el borrador y otro
    /// proceso la pasa a ConError mientras se crea. La segunda relectura, entre crear y enviar, lo ve y el borrador no sale.
    /// </summary>
    [Fact]
    public async Task Si_la_conexion_deja_de_estar_operativa_entre_crear_el_borrador_y_enviarlo_no_lo_envia()
    {
        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE").Verificada();
        var (handler, conversaciones, graphClient) = CrearHandler(conexion, configurarRepositorio: repositorio =>
            repositorio.AlReleer = vez =>
            {
                if (vez == 2)
                    repositorio.NoOperativasEnBase.Add(conexion.Id);
            });

        var resultado = await handler.Handle(
            new EnviarMensajeNuevoCommand(conexion.Id, ["contacto@tenant.com"], "Bienvenida", "<p>Hola</p>"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(CaeManager.Application.Integraciones.ErroresBuzonSinVerificar.Codigo);
        graphClient.BorradoresCreados.Should().Be(1);
        graphClient.BorradoresNoEnviados.Should().Be(1);
        graphClient.VecesEnviado.Should().Be(0);
        conversaciones.Conversaciones.Should().BeEmpty();
    }

    /// <summary>Un buzón compartido no tiene credencial: el mensaje sale por su dirección y con el token de aplicación de su directorio.</summary>
    [Fact]
    public async Task Con_un_buzon_compartido_envia_por_su_direccion_y_con_el_token_de_aplicacion_del_directorio()
    {
        var directorio = Guid.NewGuid().ToString("D");
        var conexion = ConexionIntegracion.CrearBuzonCompartido("piloto@tenant.com", "Buzón piloto", directorio, Guid.NewGuid()).Verificada();
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var graphClient = new Microsoft365GraphClientFalso();
        var conversaciones = new ConversacionRepositorioFalso();
        var handler = new EnviarMensajeNuevoCommandHandler(
            conexionRepositorio, conversaciones, new ComunicacionesQueryContextFalso(), new AlcanceDatosServiceFalso(), new CaeManager.Application.Common.InvalidadorAlcanceInerte(), graphClient,
            new AccesoGraphService(new CredencialIntegracionRepositorioFalso(), graphClient),
            new FileStorageServiceFalso(), new ResolucionParticipanteConversacionServiceFalso(), new UnitOfWorkFalso(), new RegistroExportacionFalso());

        var resultado = await handler.Handle(
            new EnviarMensajeNuevoCommand(conexion.Id, ["contacto@tenant.com"], "Bienvenida", "<p>Hola</p>"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        graphClient.BuzonesUsados.Should().ContainSingle().Which.Should().Be(new BuzonGraph("piloto@tenant.com", true));
        graphClient.DirectoriosConTokenDeAplicacion.Should().Equal(directorio);
        graphClient.VecesRefrescado.Should().Be(0, "no hay token delegado que refrescar");
        conversaciones.Conversaciones.Should().ContainSingle().Which.Mensajes
            .Should().ContainSingle(m => m.Direccion == DireccionMensaje.Saliente && m.Remitente == "piloto@tenant.com");
    }

    [Fact]
    public async Task La_copia_viaja_al_proveedor_y_queda_como_participante_Cc_del_hilo()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE").Verificada();
        var (handler, conversaciones, graphClient) = CrearHandler(conexion);

        var resultado = await handler.Handle(
            new EnviarMensajeNuevoCommand(
                conexion.Id, ["contacto@cliente.com"], "Reclamación", "<p>Hola</p>", Copia: ["coordinacion@arcosspa.example"]),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        graphClient.UltimaCopia.Should().Equal("coordinacion@arcosspa.example");
        var conversacion = conversaciones.Conversaciones.Should().ContainSingle().Which;
        conversacion.Participantes.Should().ContainSingle(p => p.Email == "contacto@cliente.com" && p.Rol == RolParticipante.Para);
        conversacion.Participantes.Should().ContainSingle(p => p.Email == "coordinacion@arcosspa.example" && p.Rol == RolParticipante.Cc);
    }

    [Fact]
    public async Task Con_conexion_deshabilitada_devuelve_fallo_y_no_crea_nada()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE").Verificada();
        conexion.Deshabilitar();
        var (handler, conversaciones, _) = CrearHandler(conexion);

        var resultado = await handler.Handle(
            new EnviarMensajeNuevoCommand(conexion.Id, ["contacto@cliente.com"], "Bienvenida", "<p>Hola</p>"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ConexionIntegracion.NoDisponible");
        conversaciones.Conversaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Si_el_envio_por_Graph_falla_no_crea_la_conversacion()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE").Verificada();
        var (handler, conversaciones, graphClient) = CrearHandler(conexion);
        graphClient.FallaEnvio = true;

        var resultado = await handler.Handle(
            new EnviarMensajeNuevoCommand(conexion.Id, ["contacto@cliente.com"], "Bienvenida", "<p>Hola</p>"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        conversaciones.Conversaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Guarda_los_adjuntos_en_el_mensaje_creado()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE").Verificada();
        var (handler, conversaciones, _) = CrearHandler(conexion);
        var adjuntos = new[] { new AdjuntoParaEnviarDto("bienvenida.pdf", "application/pdf", [9, 9]) };

        var resultado = await handler.Handle(
            new EnviarMensajeNuevoCommand(conexion.Id, ["contacto@cliente.com"], "Bienvenida", "<p>Hola</p>", Adjuntos: adjuntos),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        conversaciones.Conversaciones.Single().Mensajes.Single().Adjuntos.Should()
            .ContainSingle(a => a.NombreArchivo == "bienvenida.pdf" && a.TamanoBytes == 2);
    }

    [Fact]
    public async Task Un_entrante_con_el_mismo_hilo_externo_se_suma_al_hilo_existente_en_vez_de_crear_uno_nuevo()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE").Verificada();
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        credencialRepositorio.Agregar(new CredencialIntegracion(conexion.Id, "refresh-token"));
        var graphClient = new Microsoft365GraphClientFalso
        {
            MensajeEnviadoADevolver = new MensajeEnviadoGraphDto("msg-saliente-1", "hilo-compartido-123")
        };
        var accesoGraph = new AccesoGraphService(credencialRepositorio, graphClient);
        var conversaciones = new ConversacionRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();

        var handler = new EnviarMensajeNuevoCommandHandler(
            conexionRepositorio, conversaciones, new ComunicacionesQueryContextFalso(), new AlcanceDatosServiceFalso(), new CaeManager.Application.Common.InvalidadorAlcanceInerte(), graphClient, accesoGraph,
            new FileStorageServiceFalso(), new ResolucionParticipanteConversacionServiceFalso(), unitOfWork, new RegistroExportacionFalso());

        // 1. Enviar mensaje saliente
        var resultadoEnvio = await handler.Handle(
            new EnviarMensajeNuevoCommand(conexion.Id, ["contacto@cliente.com"], "Consulta CAE", "<p>Mensaje inicial</p>"),
            CancellationToken.None);
        resultadoEnvio.EsExitoso.Should().BeTrue();

        var conversacionOriginal = conversaciones.Conversaciones.Should().ContainSingle().Which;
        conversacionOriginal.ConexionIntegracionId.Should().Be(conexion.Id);
        conversacionOriginal.HiloExternoId.Should().Be("hilo-compartido-123");

        // 2. Simular respuesta entrante del cliente con el mismo HiloExternoId
        graphClient.MensajeIdsADevolver = ["msg-entrante-2"];
        graphClient.MensajeADevolver = new MensajeGraphDto(
            "msg-entrante-2", "hilo-compartido-123", "Re: Consulta CAE", "contacto@cliente.com", "<p>Respuesta cliente</p>",
            DateTime.UtcNow, [new ParticipanteGraphDto("contacto@cliente.com", RolParticipante.De)], []);

        var servicioIngesta = new IngestaWebhookService(
            conexionRepositorio, conversaciones, graphClient, accesoGraph,
            new FileStorageServiceFalso(),
            new SugerenciaVisitaCorreoServiceFalso(),
            new SugerenciaGestionCorreoServiceFalso(),
            new ResolucionParticipanteConversacionServiceFalso(),
            new ResolucionProveedorPlataformaCaeServiceFalso(),
            new ClasificacionRuidoMensajeRepositorioFalso(),
            new ClasificacionRuidoMensajeServiceFalso(),
            new RelevanciaCaeServiceFalso(),
            new AlertaCriticaServiceFalso(),
            NullLogger<IngestaWebhookService>.Instance, new CaeManager.Application.Tests.Comunicaciones.IntencionConversacionServiceFalso(),
            CaeManager.Application.Tests.Integraciones.ResponsableBuzonElegibleDePrueba.Con(null));

        var eventoWebhook = new EventoWebhook(conexion.Id, "{\"value\":[{}]}");
        await servicioIngesta.ProcesarAsync(eventoWebhook, CancellationToken.None);

        // 3. Afirmar que NO se ha creado una conversación nueva y que la respuesta se ha sumado al hilo
        conversaciones.Conversaciones.Should().HaveCount(1);
        conversacionOriginal.Mensajes.Should().HaveCount(2);
        conversacionOriginal.Mensajes.Should().ContainSingle(m => m.Direccion == DireccionMensaje.Saliente && m.MensajeExternoId == "msg-saliente-1");
        conversacionOriginal.Mensajes.Should().ContainSingle(m => m.Direccion == DireccionMensaje.Entrante && m.MensajeExternoId == "msg-entrante-2");
    }

    [Fact]
    public async Task Si_Graph_devuelve_un_HiloExternoId_existente_reutiliza_la_conversacion_en_vez_de_duplicarla()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE").Verificada();
        var (handler, conversaciones, graphClient) = CrearHandler(conexion);
        graphClient.MensajeEnviadoADevolver = new MensajeEnviadoGraphDto("msg-saliente-2", "hilo-existente-456");

        var conversacionPrevia = new Conversacion("Conversación anterior");
        conversacionPrevia.AsociarConexion(conexion.Id, "hilo-existente-456");
        conversaciones.Agregar(conversacionPrevia);

        var resultado = await handler.Handle(
            new EnviarMensajeNuevoCommand(conexion.Id, ["contacto@cliente.com"], "Nuevo envío", "<p>Segunda parte</p>"),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().Be(conversacionPrevia.Id);
        conversaciones.Conversaciones.Should().ContainSingle();
        conversacionPrevia.Mensajes.Should().ContainSingle(m => m.MensajeExternoId == "msg-saliente-2");
    }

    [Fact]
    public async Task Ancla_el_hilo_nuevo_a_la_Empresa_cuando_se_pide_asi()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE").Verificada();
        var (handler, conversaciones, _) = CrearHandler(conexion);
        var empresaId = Guid.NewGuid();

        var resultado = await handler.Handle(
            new EnviarMensajeNuevoCommand(
                conexion.Id, ["agenda@contratista.com"], "Documentación pendiente", "<p>…</p>", EmpresaId: empresaId),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var conversacion = conversaciones.Conversaciones.Should().ContainSingle().Subject;
        conversacion.EmpresaId.Should().Be(empresaId);
        conversacion.ClienteId.Should().BeNull(
            "sin ancla el hilo caería en la cola de triage, que ve toda la gestión CAE");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task No_escribe_en_un_hilo_existente_que_pertenece_a_otro_titular(bool ancladoACliente)
    {
        // Outlook agrupa por asunto y destinatario, así que Graph puede
        // devolver el HiloExternoId de una conversación que ya tiene dueño.
        // Sumarle el mensaje lo metería en la bandeja de quien tenga ESE
        // titular en cartera y lo escondería de quien tiene el nuestro: el
        // alcance comprobado al entrar se perdería en el último paso.
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE").Verificada();
        var (handler, conversaciones, graphClient) = CrearHandler(conexion);
        graphClient.MensajeEnviadoADevolver = new MensajeEnviadoGraphDto("msg-3", "hilo-ajeno-789");

        var otroTitular = Guid.NewGuid();
        var ajena = ancladoACliente
            ? new Conversacion("Hilo de otro cliente", clienteId: otroTitular)
            : new Conversacion("Hilo de otra empresa", clienteId: null, empresaId: otroTitular);
        ajena.AsociarConexion(conexion.Id, "hilo-ajeno-789");
        conversaciones.Agregar(ajena);

        var mando = ancladoACliente
            ? new EnviarMensajeNuevoCommand(conexion.Id, ["c@x.com"], "Asunto", "<p>…</p>", ClienteId: Guid.NewGuid())
            : new EnviarMensajeNuevoCommand(conexion.Id, ["c@x.com"], "Asunto", "<p>…</p>", EmpresaId: Guid.NewGuid());

        var resultado = await handler.Handle(mando, CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Conversacion.HiloDeOtroTitular");
        ajena.Mensajes.Should().BeEmpty("el mensaje no puede acabar en el hilo de otro titular");
    }

    [Fact]
    public async Task No_envia_desde_el_buzon_personal_de_otro_gestor_aunque_se_le_pase_su_Id()
    {
        // Regresión: sin este check, cualquiera con acceso a Comunicaciones
        // podía enviar correo desde el buzón personal de un colega pasando su
        // ConexionIntegracionId a mano — el Cliente-cartera no protege un
        // buzón personal, porque tiene ClienteId null.
        var conexionPersonal = new ConexionIntegracion(
            "gestor.otro@ejemplo.local", "Buzón personal", gestorPropietarioId: Guid.NewGuid()).Verificada();
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexionPersonal);
        var credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        credencialRepositorio.Agregar(new CredencialIntegracion(conexionPersonal.Id, "refresh-token"));
        var graphClient = new Microsoft365GraphClientFalso();
        var accesoGraph = new AccesoGraphService(credencialRepositorio, graphClient);
        var conversaciones = new ConversacionRepositorioFalso();

        var handler = new EnviarMensajeNuevoCommandHandler(
            conexionRepositorio, conversaciones, new ComunicacionesQueryContextFalso(), new AlcanceDatosServiceFalso(conexionIntegracionVisible: false), new CaeManager.Application.Common.InvalidadorAlcanceInerte(), graphClient, accesoGraph,
            new FileStorageServiceFalso(), new ResolucionParticipanteConversacionServiceFalso(), new UnitOfWorkFalso(), new RegistroExportacionFalso());

        var resultado = await handler.Handle(
            new EnviarMensajeNuevoCommand(conexionPersonal.Id, ["destinatario@cliente.com"], "Asunto", "<p>Cuerpo</p>"),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        conversaciones.Conversaciones.Should().BeEmpty();
    }
}
