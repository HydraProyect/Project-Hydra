using CaeManager.Application.Common;
using CaeManager.Application.Gestiones.Commands.CrearGestionesParaTrabajador;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Gestiones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Application.Tests.Visitas;
using CaeManager.Application.Visitas.Antelacion;
using CaeManager.Application.Visitas.Commands.CrearVisita;
using CaeManager.Application.Visitas.PaqueteDocumental;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.RelacionesEmpresariales;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Visitas;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using CurrentUserServiceFalso = CaeManager.Application.Tests.CurrentUserServiceFalso;

namespace CaeManager.Application.Tests.Comunicaciones;

/// <summary>
/// Confirmar una sugerencia de correo —crear la Visita o las Gestiones que propone— exige alcanzar la Conversación del
/// mensaje que la originó, el mismo criterio con el que ObtenerConversacionPorIdQuery la enseña. Fuera de alcance la
/// sugerencia se trata como un Id que no existe: la Visita o las Gestiones nacen, pero la sugerencia de otra cartera no
/// se resuelve, su Conversación no se enlaza y no se le envía ningún paquete documental.
/// </summary>
public class ConfirmarSugerenciasDeCorreoAlcanceCarteraTests
{
    private static readonly DateOnly Hoy = new(2026, 10, 17);

    private readonly Guid _clienteDeMiCartera = Guid.NewGuid();
    private readonly Guid _clienteDeOtraCartera = Guid.NewGuid();
    private readonly Guid _conexionDeOtroGestor = Guid.NewGuid();
    private readonly ComunicacionesQueryContextFalso _comunicaciones = new();

    private Mensaje SembrarMensajeEnConversacionDe(Guid clienteId, Guid? conexionIntegracionId = null)
    {
        var conversacion = new Conversacion("Pide visita el lunes", clienteId: clienteId);
        if (conexionIntegracionId is { } conexion)
            conversacion.AsociarConexion(conexion, "hilo-1");
        var mensaje = conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "obra@contraparte.es", "<p>El lunes.</p>");
        _comunicaciones.ListaConversaciones.Add(conversacion);
        _comunicaciones.ListaMensajes.Add(mensaje);
        return mensaje;
    }

    // ---- CrearVisitaCommand (SugerenciaVisitaCorreoId)

    private sealed class EscenarioVisita
    {
        public required CrearVisitaCommandHandler Handler { get; init; }
        public required VisitaRepositorioFalso Visitas { get; init; }
        public required PaqueteQueAnota Paquetes { get; init; }
        public required SugerenciaVisitaCorreo Sugerencia { get; init; }
        public required Centro Centro { get; init; }
        public required Trabajador Trabajador { get; init; }
        public required Trabajador TrabajadorDeOtraEmpresa { get; init; }
        public required Trabajador TrabajadorDeSubcontrata { get; init; }
        public required VisitaTrabajadoresFalsos Asignaciones { get; init; }
    }

    private EscenarioVisita PrepararVisita(
        Guid clienteDeLaConversacion, IAlcanceDatosService? alcance = null, IActorAuditoria? actor = null)
    {
        var centro = new Centro(_clienteDeMiCartera, Guid.NewGuid(), "Planta norte");
        // S-B-1: Ana es de la Empresa del Centro; Bruno, de otra Empresa del mismo Tenant; Carla, de una
        // Subcontrata proveedora vigente de la Empresa del Centro.
        var trabajador = Trabajador.DeEmpresa(centro.EmpresaId, "Ana", "García", "12345678Z");
        var deOtraEmpresa = Trabajador.DeEmpresa(Guid.NewGuid(), "Bruno", "Ajeno", "87654321X");
        var subcontrataId = Guid.NewGuid();
        var deSubcontrata = Trabajador.DeSubcontrata(subcontrataId, "Carla", "Proveedora", "11223344B");
        var centros = new CentrosQueryContextFalso();
        centros.ListaCentros.Add(centro);
        var trabajadores = new TrabajadoresQueryContextFalso();
        trabajadores.ListaTrabajadores.AddRange([trabajador, deOtraEmpresa, deSubcontrata]);
        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaRelacionesEmpresariales.Add(RelacionEmpresarial.Crear(subcontrataId, centro.EmpresaId, DateTime.UtcNow));

        var mensaje = SembrarMensajeEnConversacionDe(clienteDeLaConversacion);
        var sugerencia = new SugerenciaVisitaCorreo(mensaje.Id, centro.Id, Hoy, null, "Pide visita", 90, 90, 90);
        _comunicaciones.ListaSugerenciasVisitaCorreo.Add(sugerencia);
        var sugerencias = new SugerenciaVisitaCorreoRepositorioFalso();
        sugerencias.Agregar(sugerencia);

        var visitas = new VisitaRepositorioFalso();
        var paquetes = new PaqueteQueAnota();
        var asignaciones = new VisitaTrabajadoresFalsos();
        var handler = new CrearVisitaCommandHandler(
            visitas, asignaciones, centros, trabajadores, empresas, sugerencias, _comunicaciones, paquetes,
            new EvaluadorQueNoSella(), new CurrentUserServiceFalso(Guid.NewGuid()), new PublisherFalso(), new UnitOfWorkFalso(),
            NullLogger<CrearVisitaCommandHandler>.Instance,
            alcance ?? new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [_clienteDeMiCartera], centroIdsVisibles: [centro.Id]),
            actor);

        return new EscenarioVisita
        {
            Asignaciones = asignaciones,
            Handler = handler,
            Visitas = visitas,
            Paquetes = paquetes,
            Sugerencia = sugerencia,
            Centro = centro,
            Trabajador = trabajador,
            TrabajadorDeOtraEmpresa = deOtraEmpresa,
            TrabajadorDeSubcontrata = deSubcontrata,
        };
    }

    private static CrearVisitaCommand CrearVisitaDesde(EscenarioVisita e) =>
        new(e.Centro.Id, Hoy, Hoy, [e.Trabajador.Id], Notas: null, SugerenciaVisitaCorreoId: e.Sugerencia.Id);

    // ---- S-B-1: la Empresa gestionada activa fija qué Trabajadores entran (decisión del propietario, 2026-10-28)

    [Fact]
    public async Task CrearVisita_admite_Trabajadores_de_la_Empresa_del_Centro_y_de_su_Subcontrata_vigente()
    {
        var e = PrepararVisita(_clienteDeMiCartera);

        var resultado = await e.Handler.Handle(
            new CrearVisitaCommand(e.Centro.Id, Hoy, Hoy, [e.Trabajador.Id, e.TrabajadorDeSubcontrata.Id], Notas: null), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : "");
        e.Asignaciones.Agregadas.Select(f => f.TrabajadorId).Should().BeEquivalentTo([e.Trabajador.Id, e.TrabajadorDeSubcontrata.Id]);
    }

    [Fact]
    public async Task CrearVisita_rechaza_un_Trabajador_de_otra_Empresa_y_no_crea_la_Visita()
    {
        var e = PrepararVisita(_clienteDeMiCartera);

        var resultado = await e.Handler.Handle(
            new CrearVisitaCommand(e.Centro.Id, Hoy, Hoy, [e.Trabajador.Id, e.TrabajadorDeOtraEmpresa.Id], Notas: null), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(CaeManager.Application.Visitas.TrabajadoresAdmisiblesEnVisita.CodigoNoAdmisible);
        resultado.Error.Mensaje.Should().Contain("empresa gestionada activa");
        e.Visitas.Visitas.Should().BeEmpty();
        e.Asignaciones.Agregadas.Should().BeEmpty();
    }

    [Fact]
    public async Task CrearVisita_rechaza_un_Trabajador_de_la_Empresa_fuera_del_alcance_de_datos()
    {
        // El Centro está al alcance; de los Trabajadores, solo uno que no es Ana.
        var conAlcance = PrepararVisita(_clienteDeMiCartera, new AlcanceDatosServiceFalso(trabajadorIdsVisibles: [Guid.NewGuid()]));

        var resultado = await conAlcance.Handler.Handle(
            new CrearVisitaCommand(conAlcance.Centro.Id, Hoy, Hoy, [conAlcance.Trabajador.Id], Notas: null), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(CaeManager.Application.Visitas.TrabajadoresAdmisiblesEnVisita.CodigoNoAdmisible);
        conAlcance.Visitas.Visitas.Should().BeEmpty();
    }

    [Fact]
    public async Task CrearVisita_guarda_como_gestor_de_cada_asignacion_al_Actor_real_y_no_al_usuario_simulado()
    {
        var adminTalveg = Guid.NewGuid();
        var e = PrepararVisita(_clienteDeMiCartera, actor: new ActorFijo(
            new ActorAuditoria(adminTalveg, Guid.NewGuid(), TipoViaAcceso.SesionPrivilegiada, Guid.NewGuid())));

        (await e.Handler.Handle(CrearVisitaDesde(e), CancellationToken.None)).EsExitoso.Should().BeTrue();

        var asignacion = e.Asignaciones.Agregadas.Should().ContainSingle().Subject;
        asignacion.GestionadoPorUsuarioId.Should().Be(adminTalveg);
        asignacion.FechaAsignacionUtc.Should().NotBeNull();
    }

    private sealed class ActorFijo(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }

    [Fact]
    public async Task CrearVisita_con_una_sugerencia_de_otra_cartera_no_la_resuelve_ni_enlaza_ni_envia_su_hilo()
    {
        var e = PrepararVisita(_clienteDeOtraCartera);

        var resultado = await e.Handler.Handle(CrearVisitaDesde(e), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue("la Visita sobre un Centro de la cartera sigue naciendo, como con un Id que no existe");
        e.Sugerencia.Resuelta.Should().BeFalse();
        e.Visitas.Visitas.Single().ConversacionOrigenId.Should().BeNull();
        e.Visitas.Visitas.Single().Origen.Should().Be(OrigenVisita.Manual, "una sugerencia fuera de alcance no cuenta la Visita como «desde correo»");
        e.Paquetes.Conversaciones.Should().BeEmpty("no se envía el paquete documental a un hilo de otra cartera");
    }

    [Fact]
    public async Task CrearVisita_con_una_sugerencia_de_su_cartera_la_resuelve_y_enlaza_su_hilo()
    {
        var e = PrepararVisita(_clienteDeMiCartera);

        var resultado = await e.Handler.Handle(CrearVisitaDesde(e), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        e.Sugerencia.Resolucion.Should().Be(ResolucionSugerencia.Confirmada);
        var conversacionId = _comunicaciones.ListaConversaciones.Single().Id;
        e.Visitas.Visitas.Single().ConversacionOrigenId.Should().Be(conversacionId);
        e.Visitas.Visitas.Single().Origen.Should().Be(OrigenVisita.Correo);
        e.Paquetes.Conversaciones.Should().Equal(conversacionId);
    }

    [Fact]
    public async Task CrearVisita_sin_restriccion_de_cartera_resuelve_la_sugerencia_de_cualquier_Cliente_empresarial()
    {
        var e = PrepararVisita(_clienteDeOtraCartera, new AlcanceDatosServiceFalso(tieneAccesoTotal: true));

        var resultado = await e.Handler.Handle(CrearVisitaDesde(e), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        e.Sugerencia.Resolucion.Should().Be(ResolucionSugerencia.Confirmada);
        e.Paquetes.Conversaciones.Should().ContainSingle();
    }

    // ---- CrearGestionesParaTrabajadorCommand (DetalleSugerenciaGestionCorreoId y MensajeOrigenId)

    private (CrearGestionesParaTrabajadorCommandHandler Handler, GestionRepositorioFalso Gestiones, CrearGestionesParaTrabajadorCommand Comando, DetalleSugerenciaGestionCorreo Detalle)
        PrepararGestiones(Guid clienteDeLaConversacion, IAlcanceDatosService? alcance = null, Guid? conexionIntegracionId = null)
    {
        var trabajadorId = Guid.NewGuid();
        var centroId = Guid.NewGuid();
        var asignaciones = new AsignacionesQueryContextFalso();
        asignaciones.ListaAsignaciones.Add(new Asignacion(trabajadorId, centroId, Hoy));
        var tipo = new TipoDocumento("Formación en altura", 12, false, 1, AmbitoAplicacion.Trabajador);
        var tipos = new TipoDocumentoRepositorioFalso();
        tipos.Tipos.Add(tipo);

        var mensaje = SembrarMensajeEnConversacionDe(clienteDeLaConversacion, conexionIntegracionId);
        var sugerencia = new SugerenciaGestionCorreo(mensaje.Id, "Os mando el TC2", 90);
        var detalle = sugerencia.AgregarDetalle(trabajadorId, tipo.Id, 90, 90);
        _comunicaciones.ListaSugerenciasGestionCorreo.Add(sugerencia);
        _comunicaciones.ListaDetallesSugerenciaGestionCorreo.Add(detalle);
        var detalles = new DetallesFalsos();
        detalles.Lista.Add(detalle);

        var gestiones = new GestionRepositorioFalso();
        var handler = new CrearGestionesParaTrabajadorCommandHandler(
            asignaciones, tipos, gestiones, detalles, _comunicaciones,
            alcance ?? new AlcanceDatosServiceFalso(
                tieneAccesoTotal: false, clienteIdsVisibles: [_clienteDeMiCartera], trabajadorIdsVisibles: [trabajadorId], centroIdsVisibles: [centroId],
                conexionesIntegracionAjenas: [_conexionDeOtroGestor]),
            new CurrentUserServiceFalso(Guid.NewGuid()), new UnitOfWorkFalso());

        return (handler, gestiones, new CrearGestionesParaTrabajadorCommand(trabajadorId, tipo.Id, detalle.Id, mensaje.Id), detalle);
    }

    [Fact]
    public async Task CrearGestiones_con_un_item_de_otra_cartera_no_lo_resuelve_ni_enlaza_su_mensaje()
    {
        var (handler, gestiones, comando, detalle) = PrepararGestiones(_clienteDeOtraCartera);

        var resultado = await handler.Handle(comando, CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue("las Gestiones del Trabajador de la cartera siguen naciendo, como con un Id que no existe");
        detalle.Resuelta.Should().BeFalse();
        gestiones.Gestiones.Should().ContainSingle().Which.MensajeOrigenId.Should().BeNull();
    }

    [Fact]
    public async Task CrearGestiones_con_un_item_de_su_cartera_lo_resuelve_y_enlaza_su_mensaje()
    {
        var (handler, gestiones, comando, detalle) = PrepararGestiones(_clienteDeMiCartera);

        var resultado = await handler.Handle(comando, CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        detalle.Resolucion.Should().Be(ResolucionSugerencia.Confirmada);
        gestiones.Gestiones.Should().ContainSingle().Which.MensajeOrigenId.Should().Be(comando.MensajeOrigenId);
    }

    [Fact]
    public async Task CrearGestiones_con_un_item_de_su_cartera_y_un_mensaje_de_otra_resuelve_el_item_pero_no_enlaza_el_mensaje()
    {
        var (handler, gestiones, comando, detalle) = PrepararGestiones(_clienteDeMiCartera);
        var mensajeDeOtraCartera = SembrarMensajeEnConversacionDe(_clienteDeOtraCartera);

        var resultado = await handler.Handle(comando with { MensajeOrigenId = mensajeDeOtraCartera.Id }, CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        detalle.Resolucion.Should().Be(ResolucionSugerencia.Confirmada, "el ítem y el mensaje se comprueban cada uno por su cuenta");
        gestiones.Gestiones.Should().ContainSingle().Which.MensajeOrigenId.Should().BeNull();
    }

    [Fact]
    public async Task CrearGestiones_con_un_item_de_otra_cartera_y_un_mensaje_de_la_suya_enlaza_el_mensaje_pero_no_resuelve_el_item()
    {
        var (handler, gestiones, comando, detalle) = PrepararGestiones(_clienteDeOtraCartera);
        var mensajeDeMiCartera = SembrarMensajeEnConversacionDe(_clienteDeMiCartera);

        var resultado = await handler.Handle(comando with { MensajeOrigenId = mensajeDeMiCartera.Id }, CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        detalle.Resuelta.Should().BeFalse();
        gestiones.Gestiones.Should().ContainSingle().Which.MensajeOrigenId.Should().Be(mensajeDeMiCartera.Id);
    }

    [Fact]
    public async Task CrearGestiones_con_un_mensaje_que_no_existe_no_lo_enlaza()
    {
        var (handler, gestiones, comando, detalle) = PrepararGestiones(_clienteDeMiCartera);

        var resultado = await handler.Handle(comando with { MensajeOrigenId = Guid.NewGuid() }, CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        detalle.Resolucion.Should().Be(ResolucionSugerencia.Confirmada);
        gestiones.Gestiones.Should().ContainSingle().Which.MensajeOrigenId.Should().BeNull();
    }

    [Fact]
    public async Task CrearGestiones_con_un_item_del_buzon_personal_de_otro_Gestor_no_lo_resuelve_ni_enlaza_su_mensaje()
    {
        var (handler, gestiones, comando, detalle) = PrepararGestiones(_clienteDeMiCartera, conexionIntegracionId: _conexionDeOtroGestor);

        var resultado = await handler.Handle(comando, CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        detalle.Resuelta.Should().BeFalse("el buzón personal de otro Gestor CAE no es visible aunque el Cliente empresarial esté en la cartera");
        gestiones.Gestiones.Should().ContainSingle().Which.MensajeOrigenId.Should().BeNull();
    }

    [Fact]
    public async Task CrearGestiones_sin_restriccion_de_cartera_resuelve_el_item_de_cualquier_Cliente_empresarial()
    {
        var (handler, gestiones, comando, detalle) = PrepararGestiones(_clienteDeOtraCartera, new AlcanceDatosServiceFalso(tieneAccesoTotal: true));

        var resultado = await handler.Handle(comando, CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        detalle.Resolucion.Should().Be(ResolucionSugerencia.Confirmada);
        gestiones.Gestiones.Should().ContainSingle().Which.MensajeOrigenId.Should().Be(comando.MensajeOrigenId);
    }

    private sealed class DetallesFalsos : IDetalleSugerenciaGestionCorreoRepository
    {
        public List<DetalleSugerenciaGestionCorreo> Lista { get; } = [];

        public Task<DetalleSugerenciaGestionCorreo?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Lista.FirstOrDefault(d => d.Id == id));
    }

    private sealed class PaqueteQueAnota : IPaqueteDocumentalVisitaService
    {
        public List<Guid> Conversaciones { get; } = [];

        public Task GenerarYEnviarAsync(Guid visitaId, Guid conversacionId, CancellationToken cancellationToken = default)
        {
            Conversaciones.Add(conversacionId);
            return Task.CompletedTask;
        }

        public Task<PaqueteDocumentalZip?> ConstruirAsync(Guid visitaId, CancellationToken cancellationToken = default) =>
            Task.FromResult<PaqueteDocumentalZip?>(null);
    }

    private sealed class EvaluadorQueNoSella : IEvaluadorExpedienteVisitaService
    {
        public Task<bool> EvaluarAsync(Guid visitaId, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task EvaluarPorDocumentoAsync(Guid documentoId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class VisitaTrabajadoresFalsos : IVisitaTrabajadorRepository
    {
        public List<VisitaTrabajador> Agregadas { get; } = [];

        public Task<IReadOnlyList<VisitaTrabajador>> ObtenerPorVisitaAsync(Guid visitaId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VisitaTrabajador>>([]);

        public void Agregar(VisitaTrabajador visitaTrabajador) => Agregadas.Add(visitaTrabajador);

        public void Eliminar(VisitaTrabajador visitaTrabajador) { }
    }
}
