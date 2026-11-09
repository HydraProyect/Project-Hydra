using CaeManager.Application.Clientes;
using CaeManager.Application.Empresas;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Comunicaciones;
using CaeManager.Application.Tests.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

public class IngestaWebhookServiceTests
{
    [Theory]
    [InlineData(EstadoConversacion.Cerrada, false)]
    [InlineData(EstadoConversacion.Cerrada, true)]
    [InlineData(EstadoConversacion.Resuelta, false)]
    [InlineData(EstadoConversacion.Resuelta, true)]
    [InlineData(EstadoConversacion.EsperandoRespuestaExterna, false)]
    [InlineData(EstadoConversacion.EsperandoRespuestaExterna, true)]
    public async Task El_entrante_continua_un_hilo_cerrado_y_solo_una_respuesta_humana_reabre(
        EstadoConversacion estado, bool automatico)
    {
        var conexion = ConexionHabilitada();
        var conexiones = new ConexionIntegracionRepositorioFalso();
        conexiones.Agregar(conexion);
        var conversaciones = new ConversacionRepositorioFalso();
        var anterior = new Conversacion("Hilo anterior");
        anterior.AsociarConexion(conexion.Id, "hilo");
        anterior.CambiarEstado(estado, DateTime.UtcNow.AddDays(-1));
        conversaciones.Agregar(anterior);
        var graph = new Microsoft365GraphClientFalso
        {
            MensajeIdsADevolver = ["nuevo"],
            MensajeADevolver = new MensajeGraphDto("nuevo", "hilo", "Respuesta", "contacto@ejemplo.test", "Texto",
                DateTime.UtcNow.AddMinutes(-1), [], [], automatico ? [new("Auto-Submitted", "auto-replied")] : [])
        };
        var credenciales = new CredencialIntegracionRepositorioFalso();
        credenciales.Agregar(new CredencialIntegracion(conexion.Id, "refresh"));
        var evento = new EventoWebhook(conexion.Id, "{}");

        await CrearServicio(conexiones, conversaciones, graph, credenciales).ProcesarAsync(evento, CancellationToken.None);

        evento.Estado.Should().Be(EstadoEventoWebhook.Completado);
        if (estado == EstadoConversacion.Cerrada && automatico)
        {
            // El ruido sobre un hilo Cerrado no abre una continuación: se queda en la Cerrada.
            conversaciones.Conversaciones.Should().ContainSingle();
            anterior.Estado.Should().Be(EstadoConversacion.Cerrada);
            anterior.Mensajes.Should().ContainSingle();
        }
        else if (estado == EstadoConversacion.Cerrada)
        {
            anterior.Estado.Should().Be(EstadoConversacion.Cerrada);
            anterior.Mensajes.Should().BeEmpty();
            var nueva = conversaciones.Conversaciones.Should().HaveCount(2).And.Subject.Single(c => c.Id != anterior.Id);
            nueva.ConversacionAnteriorId.Should().Be(anterior.Id);
            nueva.Mensajes.Should().ContainSingle();
        }
        else
        {
            conversaciones.Conversaciones.Should().ContainSingle();
            anterior.Estado.Should().Be(automatico ? estado : EstadoConversacion.Abierta);
            anterior.Mensajes.Should().ContainSingle();
        }
    }

    internal static IngestaWebhookService CrearServicio(
        ConexionIntegracionRepositorioFalso conexionRepositorio,
        ConversacionRepositorioFalso conversacionRepositorio,
        Microsoft365GraphClientFalso graphClient,
        CredencialIntegracionRepositorioFalso? credencialRepositorio = null,
        ResolucionProveedorPlataformaCaeServiceFalso? resolucionPlataforma = null,
        ClasificacionRuidoMensajeRepositorioFalso? clasificacionRuidoRepositorio = null,
        DestinoCartera? responsable = null, IOperacionesQueryContext? operaciones = null, Guid? tenantActual = null,
        ILogger<IngestaWebhookService>? logger = null, IEmpresasQueryContext? empresas = null) =>
        new(conexionRepositorio, conversacionRepositorio, graphClient,
            new AccesoGraphService(credencialRepositorio ?? new CredencialIntegracionRepositorioFalso(), graphClient),
            new FileStorageServiceFalso(),
            new SugerenciaVisitaCorreoServiceFalso(),
            new SugerenciaGestionCorreoServiceFalso(),
            new ResolucionParticipanteConversacionServiceFalso(),
            resolucionPlataforma ?? new ResolucionProveedorPlataformaCaeServiceFalso(),
            clasificacionRuidoRepositorio ?? new ClasificacionRuidoMensajeRepositorioFalso(),
            new ClasificacionRuidoMensajeServiceFalso(),
            new RelevanciaCaeServiceFalso(),
            new AlertaCriticaServiceFalso(),
            logger ?? NullLogger<IngestaWebhookService>.Instance, new CaeManager.Application.Tests.Comunicaciones.IntencionConversacionServiceFalso(),
            ResponsableBuzonElegibleDePrueba.Con(responsable, operaciones, tenantActual, empresas: empresas));

    private static ConexionIntegracion ConexionHabilitada(Guid? clienteId = null)
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE", clienteId).Verificada();
        return conexion;
    }

    private static readonly DestinoCartera DestinoActivo = new(Activa: true, "GestorCae", null, false);

    private static async Task<Conversacion> IngerirHiloNuevoAsync(
        ConexionIntegracion conexion, ConversacionRepositorioFalso conversaciones,
        DestinoCartera? responsable = null, IOperacionesQueryContext? operaciones = null, Guid? tenantActual = null,
        ILogger<IngestaWebhookService>? logger = null, IEmpresasQueryContext? empresas = null)
    {
        var conexiones = new ConexionIntegracionRepositorioFalso();
        conexiones.Agregar(conexion);
        var graph = new Microsoft365GraphClientFalso
        {
            MensajeIdsADevolver = ["nuevo"],
            MensajeADevolver = new MensajeGraphDto(
                "nuevo", "hilo", "Asunto", "contacto@ejemplo.test", "Texto", DateTime.UtcNow, [], [])
        };
        var credenciales = new CredencialIntegracionRepositorioFalso();
        credenciales.Agregar(new CredencialIntegracion(conexion.Id, "refresh"));

        await CrearServicio(
                conexiones, conversaciones, graph, credenciales, responsable: responsable, operaciones: operaciones,
                tenantActual: tenantActual, logger: logger, empresas: empresas)
            .ProcesarAsync(new EventoWebhook(conexion.Id, "{}"), CancellationToken.None);

        return conversaciones.Conversaciones.Single();
    }

    /// <summary>
    /// Habilitada pero sin la identidad verificada: el evento espera sin gastar intentos (con el backoff normal se
    /// agotarían en minutos y el correo se perdería) y no se ingiere nada.
    /// </summary>
    [Fact]
    public async Task Una_conexion_habilitada_sin_verificar_deja_el_evento_en_espera_sin_gastar_intentos()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");
        conexion.FijarIdentidadBuzon("inbox-cae");
        var conexiones = new ConexionIntegracionRepositorioFalso();
        conexiones.Agregar(conexion);
        var conversaciones = new ConversacionRepositorioFalso();
        var graph = new Microsoft365GraphClientFalso
        {
            MensajeIdsADevolver = ["nuevo"],
            MensajeADevolver = new MensajeGraphDto("nuevo", "hilo", "Asunto", "contacto@ejemplo.test", "Texto", DateTime.UtcNow, [], []),
        };
        var credenciales = new CredencialIntegracionRepositorioFalso();
        credenciales.Agregar(new CredencialIntegracion(conexion.Id, "refresh"));
        var evento = new EventoWebhook(conexion.Id, "{}");
        var servicio = CrearServicio(conexiones, conversaciones, graph, credenciales);

        for (var i = 0; i < EventoWebhook.MaximoIntentos + 1; i++)
        {
            evento.MarcarEnProceso();
            await servicio.ProcesarAsync(evento, CancellationToken.None);
        }

        evento.Estado.Should().Be(EstadoEventoWebhook.Pendiente);
        evento.Intentos.Should().Be(0);
        evento.ErrorProcesado.Should().Be(IngestaWebhookService.MotivoEsperaVerificacion);
        evento.SiguienteIntentoEnUtc.Should().BeAfter(DateTime.UtcNow.AddMinutes(10));
        conversaciones.Conversaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_conexion_verificada_ingiere_el_evento_que_estaba_en_espera()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");
        conexion.FijarIdentidadBuzon("inbox-cae");
        var conversaciones = new ConversacionRepositorioFalso();
        var conexiones = new ConexionIntegracionRepositorioFalso();
        conexiones.Agregar(conexion);
        var graph = new Microsoft365GraphClientFalso
        {
            MensajeIdsADevolver = ["nuevo"],
            MensajeADevolver = new MensajeGraphDto("nuevo", "hilo", "Asunto", "contacto@ejemplo.test", "Texto", DateTime.UtcNow, [], []),
        };
        var credenciales = new CredencialIntegracionRepositorioFalso();
        credenciales.Agregar(new CredencialIntegracion(conexion.Id, "refresh"));
        var evento = new EventoWebhook(conexion.Id, "{}");
        var servicio = CrearServicio(conexiones, conversaciones, graph, credenciales);
        evento.MarcarEnProceso();
        await servicio.ProcesarAsync(evento, CancellationToken.None);

        conexion.MarcarIdentidadVerificada(DateTime.UtcNow);
        evento.MarcarEnProceso();
        await servicio.ProcesarAsync(evento, CancellationToken.None);

        evento.Estado.Should().Be(EstadoEventoWebhook.Completado);
        conversaciones.Conversaciones.Should().ContainSingle();
    }

    [Fact]
    public async Task La_conversacion_nueva_nace_asignada_al_responsable_del_buzon()
    {
        var responsable = Guid.NewGuid();
        var conexion = ConexionHabilitada();
        conexion.FijarResponsable(responsable);

        var conversacion = await IngerirHiloNuevoAsync(conexion, new ConversacionRepositorioFalso(), DestinoActivo);

        conversacion.EjecutivoAsignadoId.Should().Be(responsable);
    }

    [Fact]
    public async Task La_conversacion_del_buzon_personal_nace_asignada_a_su_propietario()
    {
        var propietario = Guid.NewGuid();
        var conexion = new ConexionIntegracion("gestor@ejemplo.test", "Personal", gestorPropietarioId: propietario).Verificada();

        var conversacion = await IngerirHiloNuevoAsync(conexion, new ConversacionRepositorioFalso(), DestinoActivo);

        conversacion.EjecutivoAsignadoId.Should().Be(propietario);
    }

    [Fact]
    public async Task Un_buzon_sin_responsable_deja_la_conversacion_sin_asignar()
    {
        var conversacion = await IngerirHiloNuevoAsync(ConexionHabilitada(), new ConversacionRepositorioFalso(), DestinoActivo);

        conversacion.EjecutivoAsignadoId.Should().BeNull();
    }

    [Theory]
    [InlineData(false)] // Ya no alcanzable desde el Tenant.
    [InlineData(true)] // Cuenta desactivada.
    public async Task Un_responsable_que_no_puede_atenderla_no_se_asigna(bool alcanzable)
    {
        var conexion = ConexionHabilitada();
        conexion.FijarResponsable(Guid.NewGuid());

        var conversacion = await IngerirHiloNuevoAsync(
            conexion, new ConversacionRepositorioFalso(), alcanzable ? DestinoActivo with { Activa = false } : null);

        conversacion.EjecutivoAsignadoId.Should().BeNull();
    }

    private sealed class LogDeAvisos : ILogger<IngestaWebhookService>
    {
        public List<(LogLevel Nivel, string Mensaje)> Entradas { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entradas.Add((logLevel, formatter(state, exception)));
    }

    [Theory]
    [InlineData("Cliente", "RolSinGestion")]
    [InlineData("GestorCae", "SinCarteraSobreLaEmpresa")]
    public async Task Quien_ya_no_puede_atender_la_Empresa_deja_la_conversacion_sin_asignar_con_motivo_en_el_log(
        string rol, string motivo)
    {
        var tenant = Guid.NewGuid();
        var responsable = Guid.NewGuid();
        var conexion = ConexionHabilitada(Guid.NewGuid());
        conexion.FijarResponsable(responsable);
        var log = new LogDeAvisos();

        var conversacion = await IngerirHiloNuevoAsync(
            conexion, new ConversacionRepositorioFalso(), new DestinoCartera(true, rol, null, false, tenant),
            tenantActual: tenant, logger: log);

        conversacion.EjecutivoAsignadoId.Should().BeNull();
        log.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Warning && e.Mensaje.Contains(motivo)
            && e.Mensaje.Contains(responsable.ToString()) && e.Mensaje.Contains(conexion.Id.ToString()));
    }

    [Fact]
    public async Task Un_Gestor_CAE_con_cartera_vigente_recibe_la_conversacion_de_la_Empresa_del_buzon()
    {
        var tenant = Guid.NewGuid();
        var responsable = Guid.NewGuid();
        var empresa = ResponsableBuzonElegibleDePrueba.ClienteEmpresarial();
        var conexion = ConexionHabilitada(empresa.Id);
        conexion.FijarResponsable(responsable);

        var conversacion = await IngerirHiloNuevoAsync(
            conexion, new ConversacionRepositorioFalso(), new DestinoCartera(true, "GestorCae", null, false, tenant),
            ResponsableBuzonElegibleDePrueba.CarteraUniversal(tenant, responsable), tenant,
            empresas: ResponsableBuzonElegibleDePrueba.EmpresasDelTenant(empresa));

        conversacion.EjecutivoAsignadoId.Should().Be(responsable);
    }

    [Fact]
    public async Task Una_cartera_universal_deja_sin_asignar_con_motivo_la_conversacion_de_una_Empresa_sin_discriminador()
    {
        var tenant = Guid.NewGuid();
        var responsable = Guid.NewGuid();
        var empresa = ResponsableBuzonElegibleDePrueba.SinDiscriminador();
        var conexion = ConexionHabilitada(empresa.Id);
        conexion.FijarResponsable(responsable);
        var log = new LogDeAvisos();

        var conversacion = await IngerirHiloNuevoAsync(
            conexion, new ConversacionRepositorioFalso(), new DestinoCartera(true, "GestorCae", null, false, tenant),
            ResponsableBuzonElegibleDePrueba.CarteraUniversal(tenant, responsable), tenant, log,
            ResponsableBuzonElegibleDePrueba.EmpresasDelTenant(empresa));

        conversacion.EjecutivoAsignadoId.Should().BeNull();
        log.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Warning && e.Mensaje.Contains("SinCarteraSobreLaEmpresa"));
    }

    [Fact]
    public async Task Una_operacion_universal_da_la_conversacion_de_un_Cliente_empresarial_al_Coordinador_CAE_delegado()
    {
        var tenant = Guid.NewGuid();
        var operador = Guid.NewGuid();
        var responsable = Guid.NewGuid();
        var empresa = ResponsableBuzonElegibleDePrueba.ClienteEmpresarial();
        var conexion = ConexionHabilitada(empresa.Id);
        conexion.FijarResponsable(responsable);

        var conversacion = await IngerirHiloNuevoAsync(
            conexion, new ConversacionRepositorioFalso(), new DestinoCartera(true, "CoordinadorCae", null, true, operador, "CoordinadorCae"),
            ResponsableBuzonElegibleDePrueba.OperacionUniversal(tenant, operador), tenant,
            empresas: ResponsableBuzonElegibleDePrueba.EmpresasDelTenant(empresa));

        conversacion.EjecutivoAsignadoId.Should().Be(responsable);
    }

    [Fact]
    public async Task Una_operacion_universal_deja_sin_asignar_con_motivo_la_conversacion_de_una_Empresa_sin_discriminador()
    {
        var tenant = Guid.NewGuid();
        var operador = Guid.NewGuid();
        var empresa = ResponsableBuzonElegibleDePrueba.SinDiscriminador();
        var conexion = ConexionHabilitada(empresa.Id);
        conexion.FijarResponsable(Guid.NewGuid());
        var log = new LogDeAvisos();

        var conversacion = await IngerirHiloNuevoAsync(
            conexion, new ConversacionRepositorioFalso(), new DestinoCartera(true, "CoordinadorCae", null, true, operador, "CoordinadorCae"),
            ResponsableBuzonElegibleDePrueba.OperacionUniversal(tenant, operador), tenant, log,
            ResponsableBuzonElegibleDePrueba.EmpresasDelTenant(empresa));

        conversacion.EjecutivoAsignadoId.Should().BeNull();
        log.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Warning && e.Mensaje.Contains("SinCarteraSobreLaEmpresa"));
    }

    [Fact]
    public async Task Un_mensaje_en_un_hilo_ya_abierto_no_reasigna_la_conversacion()
    {
        var gestora = Guid.NewGuid();
        var conexion = ConexionHabilitada();
        conexion.FijarResponsable(Guid.NewGuid());
        var existente = new Conversacion("Asunto");
        existente.AsociarConexion(conexion.Id, "hilo");
        existente.Asignar(gestora);
        var conversaciones = new ConversacionRepositorioFalso();
        conversaciones.Agregar(existente);

        await IngerirHiloNuevoAsync(conexion, conversaciones);

        existente.EjecutivoAsignadoId.Should().Be(gestora);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)] // Un responsable fijado después no entra en un hilo que seguía sin asignar.
    public async Task La_continuacion_de_un_hilo_cerrado_conserva_su_asignacion(bool estabaAsignada)
    {
        Guid? gestora = estabaAsignada ? Guid.NewGuid() : null;
        var conexion = ConexionHabilitada();
        conexion.FijarResponsable(Guid.NewGuid());
        var anterior = new Conversacion("Asunto");
        anterior.AsociarConexion(conexion.Id, "hilo");
        if (gestora is { } asignada)
            anterior.Asignar(asignada);
        anterior.CambiarEstado(EstadoConversacion.Cerrada, DateTime.UtcNow.AddDays(-1));
        var conversaciones = new ConversacionRepositorioFalso();
        conversaciones.Agregar(anterior);
        var conexiones = new ConexionIntegracionRepositorioFalso();
        conexiones.Agregar(conexion);
        var graph = new Microsoft365GraphClientFalso
        {
            MensajeIdsADevolver = ["nuevo"],
            MensajeADevolver = new MensajeGraphDto(
                "nuevo", "hilo", "Asunto", "contacto@ejemplo.test", "Texto", DateTime.UtcNow, [], [])
        };
        var credenciales = new CredencialIntegracionRepositorioFalso();
        credenciales.Agregar(new CredencialIntegracion(conexion.Id, "refresh"));

        await CrearServicio(conexiones, conversaciones, graph, credenciales, responsable: DestinoActivo)
            .ProcesarAsync(new EventoWebhook(conexion.Id, "{}"), CancellationToken.None);

        conversaciones.Conversaciones.Single(c => c.Id != anterior.Id).EjecutivoAsignadoId.Should().Be(gestora);
    }

    [Fact]
    public async Task Crea_una_conversacion_nueva_para_un_hilo_no_visto_antes()
    {
        var clienteId = Guid.NewGuid();
        var conexion = ConexionHabilitada(clienteId);
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var conversacionRepositorio = new ConversacionRepositorioFalso();
        var graphClient = new Microsoft365GraphClientFalso
        {
            MensajeIdsADevolver = ["graph-msg-1"],
            MensajeADevolver = new MensajeGraphDto(
                "graph-msg-1", "graph-thread-1", "Duda sobre CAE", "cliente@ejemplo.com", "<p>hola</p>", DateTime.UtcNow,
                [new ParticipanteGraphDto("cliente@ejemplo.com", RolParticipante.De)], []),
        };
        var credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        credencialRepositorio.Agregar(new CredencialIntegracion(conexion.Id, "refresh-token"));
        var servicio = CrearServicio(conexionRepositorio, conversacionRepositorio, graphClient, credencialRepositorio);
        var evento = new EventoWebhook(conexion.Id, "{\"value\":[{}]}");

        await servicio.ProcesarAsync(evento, CancellationToken.None);

        evento.Estado.Should().Be(EstadoEventoWebhook.Completado);
        evento.ErrorProcesado.Should().BeNull();
        conversacionRepositorio.Conversaciones.Should().ContainSingle();
        var conversacion = conversacionRepositorio.Conversaciones[0];
        conversacion.HiloExternoId.Should().Be("graph-thread-1");
        conversacion.ClienteId.Should().Be(clienteId);
        conversacion.Mensajes.Should().ContainSingle(m => m.MensajeExternoId == "graph-msg-1");
    }

    private static (ConexionIntegracion Conexion, ConexionIntegracionRepositorioFalso Repositorio) ConexionCompartida()
    {
        var conexion = ConexionIntegracion.CrearBuzonCompartido(
            "cae@operador.test", "Buzón CAE", "11111111-2222-3333-4444-555555555555", Guid.NewGuid()).Verificada();
        var repositorio = new ConexionIntegracionRepositorioFalso();
        repositorio.Agregar(conexion);
        return (conexion, repositorio);
    }

    /// <summary>Un buzón compartido se lee por <c>users/{buzón}</c> con el token de aplicación, sin credencial de nadie.</summary>
    [Fact]
    public async Task Ingiere_un_mensaje_de_un_buzon_compartido_con_el_token_de_aplicacion()
    {
        var (conexion, conexiones) = ConexionCompartida();
        var conversaciones = new ConversacionRepositorioFalso();
        var graph = new Microsoft365GraphClientFalso
        {
            MensajeIdsADevolver = ["compartido-1"],
            MensajeADevolver = new MensajeGraphDto(
                "compartido-1", "hilo-compartido", "Documentación", "contratista@ejemplo.test", "<p>adjunto</p>", DateTime.UtcNow,
                [new ParticipanteGraphDto("contratista@ejemplo.test", RolParticipante.De)], []),
        };
        var evento = new EventoWebhook(conexion.Id, "{\"value\":[{}]}");

        await CrearServicio(conexiones, conversaciones, graph).ProcesarAsync(evento, CancellationToken.None);

        evento.Estado.Should().Be(EstadoEventoWebhook.Completado);
        conversaciones.Conversaciones.Should().ContainSingle().Which.Mensajes.Should().ContainSingle(m => m.MensajeExternoId == "compartido-1");
        graph.DirectoriosConTokenDeAplicacion.Should().Equal("11111111-2222-3333-4444-555555555555");
        graph.VecesRefrescado.Should().Be(0, "un compartido nunca cae en el token de una persona");
        graph.BuzonesUsados.Should().OnlyContain(b => b == new BuzonGraph("cae@operador.test", true));
    }

    /// <summary>
    /// Un fallo de lectura que no es un 404 (403, 429, 5xx, timeout) no puede tomarse por un mensaje borrado:
    /// el evento queda para reintento y el mensaje no se pierde.
    /// </summary>
    [Fact]
    public async Task Un_fallo_transitorio_al_leer_el_mensaje_deja_el_evento_para_reintento()
    {
        var (conexion, conexiones) = ConexionCompartida();
        var conversaciones = new ConversacionRepositorioFalso();
        var graph = new Microsoft365GraphClientFalso
        {
            MensajeIdsADevolver = ["compartido-1"],
            ErrorLecturaMensaje = Error.Crear("Integraciones.Microsoft365.ErrorApi", "429"),
        };
        var evento = new EventoWebhook(conexion.Id, "{\"value\":[{}]}");

        await CrearServicio(conexiones, conversaciones, graph).ProcesarAsync(evento, CancellationToken.None);

        evento.Estado.Should().Be(EstadoEventoWebhook.Pendiente);
        evento.ErrorProcesado.Should().NotBeNullOrEmpty();
        conversaciones.Conversaciones.Should().BeEmpty();
    }

    /// <summary>
    /// Un mensaje que no se puede leer no bloquea a los siguientes del mismo evento: el sano se ingiere y el evento
    /// queda para reintento por el otro.
    /// </summary>
    [Fact]
    public async Task Un_mensaje_que_no_se_puede_leer_no_impide_ingerir_los_demas_del_lote()
    {
        var (conexion, conexiones) = ConexionCompartida();
        var conversaciones = new ConversacionRepositorioFalso();
        var graph = new Microsoft365GraphClientFalso { MensajeIdsADevolver = ["roto", "sano"] };
        graph.ErroresLecturaPorId["roto"] = Error.Crear("Integraciones.Microsoft365.ErrorApi", "403");
        graph.MensajesPorId["sano"] = new MensajeGraphDto(
            "sano", "hilo-sano", "Documentación", "contratista@ejemplo.test", "<p>ok</p>", DateTime.UtcNow,
            [new ParticipanteGraphDto("contratista@ejemplo.test", RolParticipante.De)], []);
        var evento = new EventoWebhook(conexion.Id, "{\"value\":[{}]}");

        await CrearServicio(conexiones, conversaciones, graph).ProcesarAsync(evento, CancellationToken.None);

        conversaciones.Conversaciones.Should().ContainSingle().Which.Mensajes.Should().ContainSingle(m => m.MensajeExternoId == "sano");
        evento.Estado.Should().Be(EstadoEventoWebhook.Pendiente);
        evento.ErrorProcesado.Should().Contain("403");
    }

    [Fact]
    public async Task Un_mensaje_borrado_antes_de_leerlo_se_salta_sin_reintento()
    {
        var (conexion, conexiones) = ConexionCompartida();
        var conversaciones = new ConversacionRepositorioFalso();
        var graph = new Microsoft365GraphClientFalso { MensajeIdsADevolver = ["borrado"] };
        var evento = new EventoWebhook(conexion.Id, "{\"value\":[{}]}");

        await CrearServicio(conexiones, conversaciones, graph).ProcesarAsync(evento, CancellationToken.None);

        evento.Estado.Should().Be(EstadoEventoWebhook.Completado);
        conversaciones.Conversaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Reutiliza_la_conversacion_existente_del_mismo_hilo_externo()
    {
        var conexion = ConexionHabilitada();
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var conversacionExistente = new Conversacion("Duda sobre CAE");
        conversacionExistente.AsociarConexion(conexion.Id, "graph-thread-1");
        var conversacionRepositorio = new ConversacionRepositorioFalso();
        conversacionRepositorio.Agregar(conversacionExistente);
        var graphClient = new Microsoft365GraphClientFalso
        {
            MensajeIdsADevolver = ["graph-msg-2"],
            MensajeADevolver = new MensajeGraphDto(
                "graph-msg-2", "graph-thread-1", "Duda sobre CAE", "cliente@ejemplo.com", "<p>otra vez</p>", DateTime.UtcNow, [], []),
        };
        var credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        credencialRepositorio.Agregar(new CredencialIntegracion(conexion.Id, "refresh-token"));
        var servicio = CrearServicio(conexionRepositorio, conversacionRepositorio, graphClient, credencialRepositorio);
        var evento = new EventoWebhook(conexion.Id, "{\"value\":[{}]}");

        await servicio.ProcesarAsync(evento, CancellationToken.None);

        conversacionRepositorio.Conversaciones.Should().ContainSingle();
        conversacionExistente.Mensajes.Should().ContainSingle(m => m.MensajeExternoId == "graph-msg-2");
    }

    [Fact]
    public async Task Ignora_un_mensaje_ya_ingerido_por_idempotencia()
    {
        var conexion = ConexionHabilitada();
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var conversacionExistente = new Conversacion("Duda sobre CAE");
        conversacionExistente.AsociarConexion(conexion.Id, "graph-thread-1");
        conversacionExistente.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@ejemplo.com", "<p>ya estaba</p>", mensajeExternoId: "graph-msg-1");
        var conversacionRepositorio = new ConversacionRepositorioFalso();
        conversacionRepositorio.Agregar(conversacionExistente);
        var graphClient = new Microsoft365GraphClientFalso
        {
            MensajeIdsADevolver = ["graph-msg-1"], // Graph reenvía la misma notificación
            MensajeADevolver = new MensajeGraphDto(
                "graph-msg-1", "graph-thread-1", "Duda sobre CAE", "cliente@ejemplo.com", "<p>ya estaba</p>", DateTime.UtcNow, [], []),
        };
        var credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        credencialRepositorio.Agregar(new CredencialIntegracion(conexion.Id, "refresh-token"));
        var servicio = CrearServicio(conexionRepositorio, conversacionRepositorio, graphClient, credencialRepositorio);
        var evento = new EventoWebhook(conexion.Id, "{\"value\":[{}]}");

        await servicio.ProcesarAsync(evento, CancellationToken.None);

        evento.Estado.Should().Be(EstadoEventoWebhook.Completado);
        evento.ErrorProcesado.Should().BeNull();
        conversacionExistente.Mensajes.Should().ContainSingle();
    }

    [Fact]
    public async Task No_ingiere_nada_si_la_conexion_esta_deshabilitada()
    {
        var conexion = ConexionHabilitada();
        conexion.Deshabilitar();
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var conversacionRepositorio = new ConversacionRepositorioFalso();
        var graphClient = new Microsoft365GraphClientFalso { MensajeIdsADevolver = ["graph-msg-1"] };
        var servicio = CrearServicio(conexionRepositorio, conversacionRepositorio, graphClient);
        var evento = new EventoWebhook(conexion.Id, "{\"value\":[{}]}");

        await servicio.ProcesarAsync(evento, CancellationToken.None);

        evento.Estado.Should().Be(EstadoEventoWebhook.Completado);
        conversacionRepositorio.Conversaciones.Should().BeEmpty();
    }

    /// <summary>
    /// Sesión nocturna 2026-09-02: hasta que N4 cablea <c>MarcarConError</c>
    /// desde la renovación de suscripción de Graph, este estado era
    /// inalcanzable en producción (solo lo ponía el seeder de demo) y el
    /// mismo descarte silencioso de <see cref="No_ingiere_nada_si_la_conexion_esta_deshabilitada"/>
    /// bastaba. Con esa llamada real, la notificación descartada aquí puede
    /// ser una de verdad — el comportamiento no cambia (sigue sin ingerir
    /// nada ni reintentar), pero queda registrado con un log en vez de
    /// desaparecer sin rastro.
    /// </summary>
    [Fact]
    public async Task No_ingiere_nada_si_la_conexion_esta_en_error()
    {
        var conexion = ConexionHabilitada();
        conexion.MarcarConError("fallo simulado de renovación");
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var conversacionRepositorio = new ConversacionRepositorioFalso();
        var graphClient = new Microsoft365GraphClientFalso { MensajeIdsADevolver = ["graph-msg-1"] };
        var servicio = CrearServicio(conexionRepositorio, conversacionRepositorio, graphClient);
        var evento = new EventoWebhook(conexion.Id, "{\"value\":[{}]}");

        await servicio.ProcesarAsync(evento, CancellationToken.None);

        evento.Estado.Should().Be(EstadoEventoWebhook.Completado);
        evento.ErrorProcesado.Should().BeNull("es un descarte de negocio, no un fallo transitorio que deba reintentarse");
        conversacionRepositorio.Conversaciones.Should().BeEmpty();
    }

    [Theory]
    [InlineData("mx; dmarc=pass header.from=dokify.net", true)]
    [InlineData("mx; dmarc=fail header.from=dokify.net; spf=pass smtp.mailfrom=dokify.net", false)]
    [InlineData(null, false)]
    [InlineData("mx; spf=pass smtp.mailfrom=avisos@dokify.net", true)]
    [InlineData("mx; spf=pass smtp.mailfrom=atacante.test", false)]
    [InlineData("mx; dkim=pass header.d=dokify.net", true)]
    [InlineData("mx; dkim=pass header.d=atacante.test", false)]
    [InlineData("mx; dmarc=pass header.from=atacante.test", false)]
    [InlineData("mx; dmarc=temperror header.from=dokify.net; dkim=pass header.d=dokify.net", false)]
    [InlineData("mx; dmarc=pass header.from=dokify.net; dmarc=fail header.from=dokify.net", false)]
    // Formato real de Microsoft 365, sin authserv-id.
    [InlineData("spf=pass (sender IP is 192.0.2.10) smtp.mailfrom=dokify.net; dkim=pass (signature was verified) header.d=dokify.net;dmarc=pass action=none header.from=dokify.net;compauth=pass reason=100", true)]
    [InlineData("spf=fail (sender IP is 192.0.2.66) smtp.mailfrom=atacante.test; dkim=none (message not signed) header.d=none;dmarc=fail action=none header.from=dokify.net;compauth=fail reason=000", false)]
    // Sin política DMARC publicada: decide SPF alineado o DKIM del propio dominio.
    [InlineData("mx; dmarc=none action=none header.from=dokify.net; dkim=pass header.d=dokify.net", true)]
    [InlineData("mx; dmarc=bestguesspass action=none header.from=dokify.net; spf=pass smtp.mailfrom=dokify.net", true)]
    [InlineData("mx; dmarc=none action=none header.from=dokify.net; spf=pass smtp.mailfrom=atacante.test", false)]
    // Una segunda cabecera puesta por el remitente junto a la del receptor anula la confianza.
    [InlineData("spf=fail smtp.mailfrom=atacante.test;dmarc=fail header.from=dokify.net||mx; dmarc=pass header.from=dokify.net", false)]
    [InlineData("mx; dmarc=pass header.from=dokify.net||spf=fail smtp.mailfrom=atacante.test;dmarc=fail header.from=dokify.net", false)]
    // Mayúsculas, error permanente, valor repetido y subdominio con igualdad estricta.
    [InlineData("mx; DMARC=PASS header.from=DOKIFY.NET", true)]
    [InlineData("mx; dmarc=permerror header.from=dokify.net; spf=pass smtp.mailfrom=dokify.net", false)]
    [InlineData("mx; dmarc=pass header.from=dokify.net header.from=atacante.test", false)]
    [InlineData("mx; dmarc=none header.from=dokify.net; spf=pass smtp.mailfrom=bounce.dokify.net; dkim=pass header.d=sub.dokify.net", false)]
    // Texto del remitente entrecomillado que inyectaría un segmento SPF.
    [InlineData("mx; dmarc=none header.from=dokify.net; spf=softfail smtp.mailfrom=\"x;spf=pass smtp.mailfrom=dokify.net;y=\"@atacante.test", false)]
    // Exchange marca el mensaje como no llegado por Internet: su cabecera de resultados no es de fiar.
    [InlineData("mx; dmarc=pass header.from=dokify.net||X-MS-Exchange-Organization-AuthAs: Internal", false)]
    [InlineData("mx; dmarc=pass header.from=dokify.net||X-MS-Exchange-Organization-AuthAs: Anonymous", true)]
    public async Task Una_plataforma_solo_es_de_confianza_con_autenticacion_alineada(string? autenticacion, bool confiable)
    {
        var conexion = new ConexionIntegracion("persona@empresa.test", "Personal", gestorPropietarioId: Guid.NewGuid()).Verificada();
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var conversacionRepositorio = new ConversacionRepositorioFalso();
        var cerrada = new Conversacion("Hilo cerrado");
        cerrada.AsociarConexion(conexion.Id, "graph-thread-1");
        cerrada.CambiarEstado(EstadoConversacion.Cerrada, DateTime.UtcNow.AddDays(-1));
        conversacionRepositorio.Agregar(cerrada);
        List<CabeceraCorreoDto> cabeceras = [new("Auto-Submitted", "auto-replied")];
        // «||» separa cabeceras; las que empiezan por «X-» llevan su propio nombre antes de «: ».
        foreach (var valor in autenticacion?.Split("||") ?? [])
            cabeceras.Add(valor.StartsWith("X-", StringComparison.Ordinal)
                ? new(valor[..valor.IndexOf(": ", StringComparison.Ordinal)], valor[(valor.IndexOf(": ", StringComparison.Ordinal) + 2)..])
                : new("Authentication-Results", valor));
        var graphClient = new Microsoft365GraphClientFalso
        {
            MensajeIdsADevolver = ["graph-msg-1"],
            MensajeADevolver = new MensajeGraphDto(
                "graph-msg-1", "graph-thread-1", "Aviso pendiente", "noreply@dokify.net", "<p>Aviso</p>", DateTime.UtcNow, [], [], cabeceras),
        };
        var credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        credencialRepositorio.Agregar(new CredencialIntegracion(conexion.Id, "refresh-token"));
        var proveedorId = Guid.NewGuid();
        var resolucionPlataforma = new ResolucionProveedorPlataformaCaeServiceFalso();
        resolucionPlataforma.RegistrarPlataformaPorDominioCorreo(new ProveedorPlataformaCaeCandidatoDto(proveedorId, "Dokify", true));
        var clasificacionRepositorio = new ClasificacionRuidoMensajeRepositorioFalso();
        var servicio = CrearServicio(
            conexionRepositorio, conversacionRepositorio, graphClient, credencialRepositorio,
            resolucionPlataforma, clasificacionRepositorio);
        var evento = new EventoWebhook(conexion.Id, "{\"value\":[{}]}");

        await servicio.ProcesarAsync(evento, CancellationToken.None);

        var clasificacion = clasificacionRepositorio.Clasificaciones.Should().ContainSingle().Subject;
        evento.Estado.Should().Be(EstadoEventoWebhook.Completado);
        clasificacion.EsNotificacionAutomatica.Should().Be(confiable);
        clasificacion.ProveedorPlataformaCaeId.Should().Be(confiable ? proveedorId : null);
        cerrada.Estado.Should().Be(EstadoConversacion.Cerrada);
        if (confiable)
        {
            cerrada.Mensajes.Should().ContainSingle();
            conversacionRepositorio.Conversaciones.Should().ContainSingle();
        }
        else
        {
            clasificacion.Motivo.Should().Be(MotivoRuidoMensaje.PosiblePhishing);
            cerrada.Mensajes.Should().BeEmpty();
            var continuacion = conversacionRepositorio.Conversaciones.Should().HaveCount(2).And.Subject.Single(c => c.Id != cerrada.Id);
            continuacion.ConversacionAnteriorId.Should().Be(cerrada.Id);
            continuacion.Mensajes.Should().ContainSingle();
        }
    }

    [Fact]
    public async Task Un_correo_de_remitente_desconocido_no_se_marca_como_notificacion_automatica()
    {
        var conexion = ConexionHabilitada();
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var conversacionRepositorio = new ConversacionRepositorioFalso();
        var graphClient = new Microsoft365GraphClientFalso
        {
            MensajeIdsADevolver = ["graph-msg-1"],
            MensajeADevolver = new MensajeGraphDto(
                "graph-msg-1", "graph-thread-1", "Duda sobre CAE", "cliente@ejemplo.com", "<p>hola</p>", DateTime.UtcNow, [], []),
        };
        var credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        credencialRepositorio.Agregar(new CredencialIntegracion(conexion.Id, "refresh-token"));
        var clasificacionRepositorio = new ClasificacionRuidoMensajeRepositorioFalso();
        var servicio = CrearServicio(
            conexionRepositorio, conversacionRepositorio, graphClient, credencialRepositorio,
            clasificacionRuidoRepositorio: clasificacionRepositorio);
        var evento = new EventoWebhook(conexion.Id, "{\"value\":[{}]}");

        await servicio.ProcesarAsync(evento, CancellationToken.None);

        var clasificacion = clasificacionRepositorio.Clasificaciones.Should().ContainSingle().Subject;
        clasificacion.EsNotificacionAutomatica.Should().BeFalse();
        clasificacion.ProveedorPlataformaCaeId.Should().BeNull();
    }

    [Fact]
    public async Task Registra_en_ArchivosGuardados_el_adjunto_descargado_para_poder_compensarlo_si_falla_el_guardado()
    {
        var conexion = ConexionHabilitada();
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var conversacionRepositorio = new ConversacionRepositorioFalso();
        var graphClient = new Microsoft365GraphClientFalso
        {
            MensajeIdsADevolver = ["graph-msg-1"],
            MensajeADevolver = new MensajeGraphDto(
                "graph-msg-1", "graph-thread-1", "Duda sobre CAE", "cliente@ejemplo.com", "<p>hola</p>", DateTime.UtcNow,
                [], [new AdjuntoGraphDto("adjunto-1", "informe.pdf", "application/pdf", 1024)]),
            ContenidoAdjuntoADevolver = [1, 2, 3],
        };
        var credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        credencialRepositorio.Agregar(new CredencialIntegracion(conexion.Id, "refresh-token"));
        var servicio = CrearServicio(conexionRepositorio, conversacionRepositorio, graphClient, credencialRepositorio);
        var evento = new EventoWebhook(conexion.Id, "{\"value\":[{}]}");

        await servicio.ProcesarAsync(evento, CancellationToken.None);

        servicio.ArchivosGuardados.Should().ContainSingle().Which.Should().EndWith("informe.pdf");
        var conversacion = conversacionRepositorio.Conversaciones.Should().ContainSingle().Subject;
        var mensaje = conversacion.Mensajes.Should().ContainSingle().Subject;
        mensaje.Adjuntos.Should().ContainSingle(a => a.ArchivoUrl == servicio.ArchivosGuardados[0]);
    }

    [Fact]
    public async Task Registra_el_fallo_sin_marcar_procesado_si_falla_el_refresco_del_token()
    {
        var conexion = ConexionHabilitada();
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var conversacionRepositorio = new ConversacionRepositorioFalso();
        var graphClient = new Microsoft365GraphClientFalso { MensajeIdsADevolver = ["graph-msg-1"], FallaRefresco = true };
        var credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        credencialRepositorio.Agregar(new CredencialIntegracion(conexion.Id, "refresh-token"));
        var servicio = CrearServicio(conexionRepositorio, conversacionRepositorio, graphClient, credencialRepositorio);
        var evento = new EventoWebhook(conexion.Id, "{\"value\":[{}]}");

        await servicio.ProcesarAsync(evento, CancellationToken.None);

        evento.Estado.Should().Be(EstadoEventoWebhook.Pendiente);
        evento.Intentos.Should().Be(1);
        evento.ErrorProcesado.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ClasificarCorreoBuzonPersonal_marca_correo_interno_cuando_comparte_dominio_con_el_buzon()
    {
        IngestaWebhookService.ClasificarCorreoBuzonPersonal("rrhh@arcosspa.com", "gestor@arcosspa.com")
            .Should().Be(MotivoRuidoMensaje.CorreoInterno);
    }

    [Fact]
    public void ClasificarCorreoBuzonPersonal_marca_correo_interno_para_un_subdominio_propio()
    {
        IngestaWebhookService.ClasificarCorreoBuzonPersonal("notificaciones@mail.arcosspa.com", "gestor@arcosspa.com")
            .Should().Be(MotivoRuidoMensaje.CorreoInterno);
    }

    [Fact]
    public void ClasificarCorreoBuzonPersonal_marca_posible_phishing_para_un_dominio_ajeno()
    {
        IngestaWebhookService.ClasificarCorreoBuzonPersonal("alguien@dominio-desconocido.com", "gestor@arcosspa.com")
            .Should().Be(MotivoRuidoMensaje.PosiblePhishing);
    }

    [Fact]
    public void ClasificarCorreoBuzonPersonal_marca_posible_phishing_si_el_remitente_no_tiene_forma_de_email()
    {
        IngestaWebhookService.ClasificarCorreoBuzonPersonal("no-es-un-email", "gestor@arcosspa.com")
            .Should().Be(MotivoRuidoMensaje.PosiblePhishing);
    }
}
