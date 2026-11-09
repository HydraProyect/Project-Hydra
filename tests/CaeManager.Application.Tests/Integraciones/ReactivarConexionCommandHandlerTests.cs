using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Commands.ReactivarConexion;
using CaeManager.Domain.Common;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

public class ReactivarConexionCommandHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    private static ReactivarConexionCommandHandler CrearHandler(
        ConexionIntegracionRepositorioFalso conexionRepositorio, AlcanceDatosServiceFalso alcanceDatos,
        UnitOfWorkFalso unitOfWork, ReclamacionBuzonIntegracionRepositorioFalso? reclamacionRepositorio = null,
        ITenantActual? tenantActual = null, Microsoft365GraphClientFalso? graph = null, EventoWebhookRepositorioFalso? eventos = null,
        CerrojoVerificacionBuzonFalso? cerrojo = null)
    {
        graph ??= new Microsoft365GraphClientFalso { IdentidadBuzonPropio = "inbox-x" };
        eventos ??= new EventoWebhookRepositorioFalso();
        return new(conexionRepositorio, reclamacionRepositorio ?? new ReclamacionBuzonIntegracionRepositorioFalso(),
            alcanceDatos, tenantActual ?? new TenantActualFalso(TenantId), unitOfWork,
            new AccesoGraphService(new CredencialIntegracionRepositorioFalso { CredencialParaCualquierConexion = true }, graph), graph,
            eventos, cerrojo ?? new CerrojoVerificacionBuzonFalso());
    }

    [Fact]
    public async Task Reactivar_desde_ConError_marca_verificada_libera_los_retenidos_y_deja_la_marca_sin_leer_Graph()
    {
        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE");
        conexion.FijarIdentidadBuzon("inbox-x");
        var caida = DateTime.UtcNow.AddHours(-2);
        conexion.MarcarConError("El refresh token ha expirado.", caida);
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var eventos = new EventoWebhookRepositorioFalso();
        var retenido = new EventoWebhook(conexion.Id, "{}");
        retenido.EsperarVerificacion("en espera", DateTime.UtcNow + EventoWebhook.TopeEsperaVerificacion + TimeSpan.FromMinutes(1));
        eventos.Agregar(retenido);
        var graph = new Microsoft365GraphClientFalso
        {
            IdentidadBuzonPropio = "inbox-x",
            MensajesADevolver = [new MensajeResumenGraphDto("m-1", "h-1", "Asunto", "remitente@tenant.com", DateTime.UtcNow.AddMinutes(1), false)],
        };
        var handler = CrearHandler(conexionRepositorio, new AlcanceDatosServiceFalso(), new UnitOfWorkFalso(), graph: graph, eventos: eventos);

        var resultado = await handler.Handle(new ReactivarConexionCommand(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        conexion.OperativaVerificada(DateTime.UtcNow).Should().BeTrue();
        retenido.Estado.Should().Be(EstadoEventoWebhook.Pendiente);
        graph.ListadosPedidos.Should().BeEmpty("la rehabilitación no lee el buzón: el hueco queda declarado y no se recupera");
        eventos.Eventos.Should().Equal(retenido);
        conexion.HuecoPendienteDesdeUtc.Should().Be(caida);
    }

    /// <summary>Lo que llegó con la conexión desconectada a propósito no se ingiere al reactivarla.</summary>
    [Fact]
    public async Task Reactivar_desde_Deshabilitada_marca_verificada_sin_recuperar_el_hueco()
    {
        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE");
        conexion.FijarIdentidadBuzon("inbox-x");
        conexion.Deshabilitar();
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var eventos = new EventoWebhookRepositorioFalso();
        var graph = new Microsoft365GraphClientFalso
        {
            IdentidadBuzonPropio = "inbox-x",
            MensajesADevolver = [new MensajeResumenGraphDto("m-1", "h-1", "Asunto", "remitente@tenant.com", DateTime.UtcNow.AddMinutes(1), false)],
        };
        var handler = CrearHandler(conexionRepositorio, new AlcanceDatosServiceFalso(), new UnitOfWorkFalso(), graph: graph, eventos: eventos);

        var resultado = await handler.Handle(new ReactivarConexionCommand(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        conexion.OperativaVerificada(DateTime.UtcNow).Should().BeTrue();
        graph.ListadosPedidos.Should().BeEmpty();
        eventos.Eventos.Should().BeEmpty();
    }

    /// <summary>
    /// Reactivar una Habilitada cuya verificación ya caducó: la renueva y no marca hueco. La caducidad sola bloquea, pero
    /// no pierde correo: los eventos esperaban en la cola.
    /// </summary>
    [Fact]
    public async Task Reactivar_una_habilitada_con_la_verificacion_caducada_la_renueva_sin_fijar_marca()
    {
        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE");
        conexion.FijarIdentidadBuzon("inbox-x");
        conexion.MarcarIdentidadVerificada(DateTime.UtcNow - ConexionIntegracion.VentanaVerificacionIdentidad - TimeSpan.FromHours(3));
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(conexionRepositorio, new AlcanceDatosServiceFalso(), unitOfWork);

        var resultado = await handler.Handle(new ReactivarConexionCommand(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        conexion.OperativaVerificada(DateTime.UtcNow).Should().BeTrue();
        conexion.HuecoPendienteDesdeUtc.Should().BeNull("la caducidad sola no es pérdida: el correo esperaba en la cola");
        unitOfWork.VecesGuardado.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// Otra verificación de la misma conexión en curso (el ciclo diario o el reintento, en cualquier réplica): Reactivar
    /// no llama a Microsoft ni cambia nada, y pide reintentarlo.
    /// </summary>
    [Fact]
    public async Task Con_otra_verificacion_en_curso_Reactivar_no_comprueba_y_pide_reintentar()
    {
        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE");
        conexion.FijarIdentidadBuzon("inbox-x");
        conexion.MarcarConError("fallo");
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var graph = new Microsoft365GraphClientFalso { IdentidadBuzonPropio = "inbox-x" };
        var cerrojo = new CerrojoVerificacionBuzonFalso();
        cerrojo.Ocupadas.Add(conexion.Id);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(conexionRepositorio, new AlcanceDatosServiceFalso(), unitOfWork, graph: graph, cerrojo: cerrojo);

        var resultado = await handler.Handle(new ReactivarConexionCommand(conexion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(ReactivarConexionCommandHandler.CodigoVerificacionEnCurso);
        conexion.Estado.Should().Be(EstadoConexionIntegracion.ConError);
        graph.TokensDeIdentidad.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Rehabilita_una_conexion_con_error_y_limpia_el_ultimo_error()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");
        conexion.FijarIdentidadBuzon("inbox-x");
        conexion.MarcarConError("El refresh token ha expirado.");
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(conexionRepositorio, new AlcanceDatosServiceFalso(), unitOfWork);

        var resultado = await handler.Handle(new ReactivarConexionCommand(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
        conexion.UltimoError.Should().BeNull();
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    /// <summary>
    /// Una colisión de identidad en el relleno deja la conexión ConError y sin identidad: Reactivar no la levanta,
    /// porque desde ConError no se vuelve a reclamar nada. Hay que reconectar, y ahí decide el índice único.
    /// </summary>
    [Fact]
    public async Task No_rehabilita_desde_ConError_una_conexion_sin_identidad_del_buzon()
    {
        var conexion = new ConexionIntegracion("alias@cliente.com", "Buzón CAE");
        conexion.MarcarConError(CompletarIdentidadBuzonService.MensajeBuzonDuplicado);
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(conexionRepositorio, new AlcanceDatosServiceFalso(), unitOfWork);

        var resultado = await handler.Handle(new ReactivarConexionCommand(conexion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(ReactivarConexionCommandHandler.CodigoReconexionNecesaria);
        conexion.Estado.Should().Be(EstadoConexionIntegracion.ConError);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task No_rehabilita_si_la_direccion_responde_ahora_con_otro_buzon()
    {
        var conexion = ConexionIntegracion.CrearBuzonCompartido(
            "cae@cliente.com", "Buzón CAE", "11111111-2222-3333-4444-555555555555", Guid.NewGuid());
        conexion.FijarIdentidadBuzon("inbox-original");
        conexion.MarcarConError(CompletarIdentidadBuzonService.MensajeBuzonCambiado);
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var graph = new Microsoft365GraphClientFalso();
        graph.IdentidadesPorDireccion["cae@cliente.com"] = "inbox-de-otro-buzon";
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(conexionRepositorio, new AlcanceDatosServiceFalso(), unitOfWork, graph: graph);

        var resultado = await handler.Handle(new ReactivarConexionCommand(conexion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(ReactivarConexionCommandHandler.CodigoReconexionNecesaria);
        conexion.Estado.Should().Be(EstadoConexionIntegracion.ConError);
        graph.TokensDeIdentidad.Should().Equal("token-aplicacion-falso");
        unitOfWork.VecesGuardado.Should().Be(1, "el motivo definitivo se guarda, igual que en el ciclo");
    }

    /// <summary>
    /// Una conexión operativa cuya dirección responde ahora con otro buzón deja de estar operativa ya, con el mismo
    /// motivo que pone el ciclo de verificación, y queda marcado el hueco desde ese momento.
    /// </summary>
    [Fact]
    public async Task Reactivar_una_conexion_operativa_cuyo_buzon_cambio_la_pasa_a_ConError_con_el_motivo_del_ciclo()
    {
        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE");
        conexion.FijarIdentidadBuzon("inbox-original");
        conexion.MarcarIdentidadVerificada(DateTime.UtcNow.AddHours(-1));
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var graph = new Microsoft365GraphClientFalso();
        graph.IdentidadesPorDireccion["cae@operador.test"] = "inbox-de-otro-buzon";
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(conexionRepositorio, new AlcanceDatosServiceFalso(), unitOfWork, graph: graph);
        var antes = DateTime.UtcNow;

        var resultado = await handler.Handle(new ReactivarConexionCommand(conexion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        conexion.Estado.Should().Be(EstadoConexionIntegracion.ConError);
        conexion.UltimoError.Should().Be(CompletarIdentidadBuzonService.MensajeBuzonCambiado);
        conexion.OperativaVerificada(DateTime.UtcNow).Should().BeFalse();
        conexion.HuecoPendienteDesdeUtc.Should().BeOnOrAfter(antes);
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    /// <summary>Desde Deshabilitada no: sigue desconectada y no se guarda nada.</summary>
    [Fact]
    public async Task Reactivar_desde_Deshabilitada_con_el_buzon_cambiado_no_la_pasa_a_ConError()
    {
        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE");
        conexion.FijarIdentidadBuzon("inbox-original");
        conexion.Deshabilitar();
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var graph = new Microsoft365GraphClientFalso();
        graph.IdentidadesPorDireccion["cae@operador.test"] = "inbox-de-otro-buzon";
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(conexionRepositorio, new AlcanceDatosServiceFalso(), unitOfWork, graph: graph);

        var resultado = await handler.Handle(new ReactivarConexionCommand(conexion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Deshabilitada);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task No_rehabilita_si_no_puede_comprobar_el_buzon()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");
        conexion.FijarIdentidadBuzon("inbox-x");
        conexion.MarcarConError("fallo");
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var graph = new Microsoft365GraphClientFalso { ErrorIdentidadBuzon = Error.Crear("Integraciones.Microsoft365.TiempoAgotado", "timeout") };
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(conexionRepositorio, new AlcanceDatosServiceFalso(), unitOfWork, graph: graph);

        var resultado = await handler.Handle(new ReactivarConexionCommand(conexion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(ReactivarConexionCommandHandler.CodigoSinComprobacion);
        conexion.Estado.Should().Be(EstadoConexionIntegracion.ConError);
    }

    /// <summary>
    /// 403/404 es definitivo, con la misma clasificación que el ciclo: una Habilitada (la pantalla aún mostraba ConError,
    /// pero el ciclo ya la había rehabilitado) pasa a ConError con el motivo del ciclo y la marca del hueco. No se pide
    /// «inténtalo de nuevo».
    /// </summary>
    [Fact]
    public async Task Sin_acceso_al_buzon_una_habilitada_pasa_a_ConError_con_el_motivo_del_ciclo_y_la_marca()
    {
        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE");
        conexion.FijarIdentidadBuzon("inbox-x");
        conexion.MarcarIdentidadVerificada(DateTime.UtcNow.AddHours(-1));
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var graph = new Microsoft365GraphClientFalso { ErrorIdentidadBuzon = Error.Crear(CodigosErrorMicrosoft365.SinAccesoBuzon, "403") };
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(conexionRepositorio, new AlcanceDatosServiceFalso(), unitOfWork, graph: graph);

        var resultado = await handler.Handle(new ReactivarConexionCommand(conexion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(ReactivarConexionCommandHandler.CodigoSinAccesoBuzon);
        conexion.Estado.Should().Be(EstadoConexionIntegracion.ConError);
        conexion.UltimoError.Should().Be(CompletarIdentidadBuzonService.MensajeSinAccesoBuzon);
        conexion.HuecoPendienteDesdeUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1), "pasar a ConError marca (disparador c)");
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Sin_acceso_al_buzon_conserva_el_motivo_de_identidad_mas_concreto()
    {
        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE");
        conexion.FijarIdentidadBuzon("inbox-x");
        conexion.MarcarConError(CompletarIdentidadBuzonService.MensajeBuzonCambiado);
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var graph = new Microsoft365GraphClientFalso { ErrorIdentidadBuzon = Error.Crear(CodigosErrorMicrosoft365.SinAccesoBuzon, "404") };
        var handler = CrearHandler(conexionRepositorio, new AlcanceDatosServiceFalso(), new UnitOfWorkFalso(), graph: graph);

        var resultado = await handler.Handle(new ReactivarConexionCommand(conexion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ReactivarConexionCommandHandler.CodigoSinAccesoBuzon);
        conexion.Estado.Should().Be(EstadoConexionIntegracion.ConError);
        conexion.UltimoError.Should().Be(CompletarIdentidadBuzonService.MensajeBuzonCambiado);
    }

    /// <summary>Desde Deshabilitada, un 403/404 no la mueve: sigue desconectada, sin reclamación ni marca.</summary>
    [Fact]
    public async Task Sin_acceso_al_buzon_una_deshabilitada_sigue_deshabilitada_sin_guardar()
    {
        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE");
        conexion.FijarIdentidadBuzon("inbox-x");
        conexion.Deshabilitar();
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var reclamaciones = new ReclamacionBuzonIntegracionRepositorioFalso();
        var graph = new Microsoft365GraphClientFalso { ErrorIdentidadBuzon = Error.Crear(CodigosErrorMicrosoft365.SinAccesoBuzon, "403") };
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(conexionRepositorio, new AlcanceDatosServiceFalso(), unitOfWork, reclamaciones, graph: graph);

        var resultado = await handler.Handle(new ReactivarConexionCommand(conexion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ReactivarConexionCommandHandler.CodigoSinAccesoBuzon);
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Deshabilitada);
        conexion.UltimoError.Should().BeNull();
        conexion.HuecoPendienteDesdeUtc.Should().BeNull();
        reclamaciones.Reclamaciones.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>Un transitorio (429, 5xx, 401, tiempo agotado) no dice nada del buzón: una Habilitada no cambia.</summary>
    [Theory]
    [InlineData("Integraciones.Microsoft365.TiempoAgotado")]
    [InlineData("Integraciones.Microsoft365.ErrorApi")]
    [InlineData("Integraciones.Microsoft365.Limitado")]
    public async Task Un_transitorio_no_cambia_una_habilitada_y_pide_reintentar(string codigo)
    {
        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE");
        conexion.FijarIdentidadBuzon("inbox-x");
        conexion.MarcarIdentidadVerificada(DateTime.UtcNow.AddHours(-1));
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var graph = new Microsoft365GraphClientFalso { ErrorIdentidadBuzon = Error.Crear(codigo, "fallo") };
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(conexionRepositorio, new AlcanceDatosServiceFalso(), unitOfWork, graph: graph);

        var resultado = await handler.Handle(new ReactivarConexionCommand(conexion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ReactivarConexionCommandHandler.CodigoSinComprobacion);
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
        conexion.HuecoPendienteDesdeUtc.Should().BeNull();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Rechaza_una_conexion_que_no_existe()
    {
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(conexionRepositorio, new AlcanceDatosServiceFalso(), unitOfWork);

        var resultado = await handler.Handle(new ReactivarConexionCommand(Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ConexionIntegracion.NoEncontrada");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Rechaza_una_conexion_fuera_de_la_cartera_visible()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE", clienteId: Guid.NewGuid());
        conexion.MarcarConError("fallo");
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(
            conexionRepositorio, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [Guid.NewGuid()]), unitOfWork);

        var resultado = await handler.Handle(new ReactivarConexionCommand(conexion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        conexion.Estado.Should().Be(EstadoConexionIntegracion.ConError, "un cliente fuera de la cartera visible no debe poder tocar la conexión");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task No_reactiva_el_buzon_personal_de_otro_gestor_aunque_se_le_pase_su_Id()
    {
        // Mismo hallazgo que EnviarMensajeNuevoCommandHandlerTests: un buzón
        // personal tiene ClienteId null, igual que el genérico del tenant —
        // sin ConexionIntegracionVisibleAsync, la cartera de Cliente no lo
        // protege de un tercero que conozca/adivine su Id.
        var conexionPersonal = new ConexionIntegracion(
            "gestor.otro@ejemplo.local", "Buzón personal", gestorPropietarioId: Guid.NewGuid());
        conexionPersonal.MarcarConError("fallo");
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexionPersonal);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(conexionRepositorio, new AlcanceDatosServiceFalso(conexionIntegracionVisible: false), unitOfWork);

        var resultado = await handler.Handle(new ReactivarConexionCommand(conexionPersonal.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ConexionIntegracion.NoEncontrada");
        conexionPersonal.Estado.Should().Be(EstadoConexionIntegracion.ConError);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// Incremento 1 de PROPUESTA-BUZONES-COMPARTIDOS-M365 § 5.4: Deshabilitada
    /// es la única transición que libera la reclamación del buzón (ver
    /// DesconectarBuzonCommandHandlerTests), así que reactivar desde ahí debe
    /// volver a reclamarlo — a diferencia de ConError, que nunca la liberó.
    /// </summary>
    [Fact]
    public async Task Al_reactivar_desde_Deshabilitada_vuelve_a_reclamar_el_buzon()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");
        conexion.FijarIdentidadBuzon("inbox-x");
        conexion.Deshabilitar();
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var reclamacionRepositorio = new ReclamacionBuzonIntegracionRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(conexionRepositorio, new AlcanceDatosServiceFalso(), unitOfWork, reclamacionRepositorio);

        var resultado = await handler.Handle(new ReactivarConexionCommand(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
        reclamacionRepositorio.Reclamaciones.Should().ContainSingle(
            r => r.BuzonEmail == "cae@cliente.com" && r.TenantPropietarioId == TenantId && r.ConexionIntegracionId == conexion.Id &&
                 r.IdentidadBuzon == "inbox-x");
        reclamacionRepositorio.VecesGuardado.Should().Be(1);
    }

    /// <summary>
    /// Una conexión Deshabilitada anterior a la identidad del buzón no la tiene, y reclamar solo por la dirección
    /// dejaría pasar un alias del mismo buzón conectado en otro Tenant: se pide volver a conectarla.
    /// </summary>
    [Fact]
    public async Task Rechaza_reactivar_desde_Deshabilitada_una_conexion_sin_identidad_del_buzon()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");
        conexion.Deshabilitar();
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var reclamacionRepositorio = new ReclamacionBuzonIntegracionRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(conexionRepositorio, new AlcanceDatosServiceFalso(), unitOfWork, reclamacionRepositorio);

        var resultado = await handler.Handle(new ReactivarConexionCommand(conexion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Integraciones.Microsoft365.ReconexionNecesaria");
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Deshabilitada);
        reclamacionRepositorio.Reclamaciones.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// Mientras la conexión estaba Deshabilitada, otro tenant reclamó el
    /// mismo buzón — reactivar no puede colar una segunda reclamación.
    /// </summary>
    [Fact]
    public async Task Rechaza_reactivar_si_otro_tenant_reclamo_el_buzon_mientras_estaba_desconectada()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");
        conexion.FijarIdentidadBuzon("inbox-x");
        conexion.Deshabilitar();
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var reclamacionRepositorio = new ReclamacionBuzonIntegracionRepositorioFalso { BuzonYaReclamado = true };
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(conexionRepositorio, new AlcanceDatosServiceFalso(), unitOfWork, reclamacionRepositorio);

        var resultado = await handler.Handle(new ReactivarConexionCommand(conexion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Integraciones.Microsoft365.BuzonYaConectado");
        reclamacionRepositorio.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// Hallazgo del punto 6 de la revisión Codex sobre PR #820: antes de este
    /// fix, Rehabilitar() se llamaba ANTES de comprobar TenantId, dejando la
    /// entidad Habilitada en memoria (trackeada, sin guardar) aunque el
    /// Command devolviera fallo — riesgo real porque el DbContext es scoped
    /// por circuito Blazor, no por request.
    /// </summary>
    [Fact]
    public async Task No_rehabilita_la_conexion_si_no_se_puede_resolver_el_tenant_actual()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");
        conexion.FijarIdentidadBuzon("inbox-x");
        conexion.Deshabilitar();
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var reclamacionRepositorio = new ReclamacionBuzonIntegracionRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(
            conexionRepositorio, new AlcanceDatosServiceFalso(), unitOfWork, reclamacionRepositorio,
            tenantActual: new TenantActualFalso(null));

        var resultado = await handler.Handle(new ReactivarConexionCommand(conexion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Integraciones.Microsoft365.TenantNoResuelto");
        conexion.Estado.Should().Be(
            EstadoConexionIntegracion.Deshabilitada, "no debe quedar Habilitada en memoria sin haberse podido guardar ni reclamar");
        reclamacionRepositorio.VecesGuardado.Should().Be(0);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    private sealed class TenantActualFalso(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }
}
