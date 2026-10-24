using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.AlertasCriticas;
using CaeManager.Application.Comunicaciones.Deteccion;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Incidencias;
using CaeManager.Application.Tests.Notificaciones;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Incidencias;
using CaeManager.Domain.Plataforma;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaeManager.Application.Tests.Comunicaciones;

public class AlertaCriticaServiceTests
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _direccion = Guid.NewGuid();
    private readonly Guid _coordinador = Guid.NewGuid();
    private readonly Repositorio _repositorio = new();
    private readonly NotificacionUsuarioRepositorioFalso _avisos = new();
    private readonly Opciones _opciones = new();
    private readonly Opinion _opinion = new();

    private bool _nivel0 = true;

    private AlertaCriticaService Crear() => new(_repositorio, _avisos, new Directorio(_direccion, _coordinador),
        new Tenant(_tenant), _opciones, _opinion, new Instruccion(_nivel0), NullLogger<AlertaCriticaService>.Instance);

    [Fact]
    public async Task Sin_Nivel_0_la_alerta_determinista_sale_pero_el_texto_no_va_a_la_IA()
    {
        _nivel0 = false;
        var mensaje = new Mensaje(Guid.NewGuid(), DireccionMensaje.Entrante, CanalConversacion.Correo, "empresa@example.test", "Inspección de Trabajo", DateTime.UtcNow);
        await Crear().ProcesarAsync(mensaje, default);
        _opinion.Llamadas.Should().Be(0);
        _repositorio.Alertas.Should().ContainSingle().Which.ConfianzaIa.Should().BeNull();
        _avisos.Notificaciones.Should().HaveCount(2);
    }

    [Fact]
    public async Task Un_entrante_automatico_no_alerta()
    {
        var conversacion = new Conversacion("Fuera de la oficina");
        await Crear().ProcesarAsync(conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo,
            "empresa@example.test", "Podrá dar lugar a acciones legales", DateTime.UtcNow, esAutomatico: true), default);
        _repositorio.Alertas.Should().BeEmpty();
        _avisos.Notificaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_fallo_del_repositorio_no_rompe_la_ingesta_pero_la_cancelacion_si_se_propaga()
    {
        _repositorio.Lanza = true;
        var mensaje = new Mensaje(Guid.NewGuid(), DireccionMensaje.Entrante, CanalConversacion.Correo, "empresa@example.test", "Inspección de Trabajo", DateTime.UtcNow);
        await Crear().Invoking(s => s.ProcesarAsync(mensaje, default)).Should().NotThrowAsync();
        using var cancelado = new CancellationTokenSource();
        await cancelado.CancelAsync();
        await Crear().Invoking(s => s.ProcesarAsync(mensaje, cancelado.Token)).Should().ThrowAsync<Exception>();
    }

    private sealed class Instruccion(bool habilitada) : CaeManager.Application.Cumplimiento.IInstruccionTratamientoIaService
    {
        public Task<bool> EstaHabilitadaAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.FromResult(habilitada);
    }

    [Fact]
    public async Task En_triaje_hay_una_sola_alerta_y_solo_avisa_a_los_dos_perfiles_de_direccion()
    {
        var conversacion = new Conversacion("Requerimiento");
        var servicio = Crear();
        foreach (var texto in new[] { "Inspección de Trabajo", "Denuncia" })
            await servicio.ProcesarAsync(conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "empresa@example.test", texto, DateTime.UtcNow), default);
        _repositorio.Alertas.Should().ContainSingle().Which.OperadorCaeTenantId.Should().Be(_tenant);
        _avisos.Notificaciones.Select(n => n.UsuarioDestinatarioId).Should().BeEquivalentTo([_direccion, _coordinador]);
        _repositorio.Alertas[0].Estado.Should().Be(EstadoAlertaCritica.Pendiente);
        _repositorio.Alertas[0].ConfianzaIa.Should().Be(81);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Una_alerta_ya_resuelta_no_silencia_un_caso_critico_nuevo_del_mismo_hilo(bool confirmada)
    {
        var conversacion = new Conversacion("Hilo largo con la Empresa gestionada");
        var servicio = Crear();
        await servicio.ProcesarAsync(conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "empresa@example.test", "Denuncia", DateTime.UtcNow), default);
        _repositorio.Alertas[0].Resolver(confirmada, _direccion, DateTime.UtcNow);
        _avisos.Notificaciones.Clear();

        await servicio.ProcesarAsync(conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "empresa@example.test", "Requerimiento oficial de la Inspección de Trabajo", DateTime.UtcNow), default);
        await servicio.ProcesarAsync(conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "empresa@example.test", "Sanción", DateTime.UtcNow), default);

        _repositorio.Alertas.Should().HaveCount(2);
        _repositorio.Alertas[1].Estado.Should().Be(EstadoAlertaCritica.Pendiente);
        _repositorio.Alertas[1].MensajeId.Should().NotBe(_repositorio.Alertas[0].MensajeId);
        _avisos.Notificaciones.Select(n => n.UsuarioDestinatarioId).Should().BeEquivalentTo([_direccion, _coordinador]);
    }

    [Fact]
    public async Task Una_segunda_opinion_fallida_no_suprime_la_alerta()
    {
        _opinion.Falla = true;
        var mensaje = new Mensaje(Guid.NewGuid(), DireccionMensaje.Entrante, CanalConversacion.WhatsApp, "+34600000000", "Sanció", DateTime.UtcNow);
        await Crear().ProcesarAsync(mensaje, default);
        _repositorio.Alertas.Should().ContainSingle().Which.ConfianzaIa.Should().BeNull();
        _avisos.Notificaciones.Should().HaveCount(2);
    }

    [Fact]
    public async Task Un_saliente_no_consulta_ni_la_IA_ni_el_directorio()
    {
        await Crear().ProcesarAsync(new Mensaje(Guid.NewGuid(), DireccionMensaje.Saliente, CanalConversacion.Correo,
            "propio@example.test", "Inspección de Trabajo", DateTime.UtcNow), default);
        _repositorio.Alertas.Should().BeEmpty();
        _avisos.Notificaciones.Should().BeEmpty();
        _opinion.Llamadas.Should().Be(0);
    }

    [Fact]
    public async Task Las_reglas_son_independientes_por_Operador_Cae()
    {
        _opciones.CurrentValue.ExpresionesPorOperadorCae[_tenant] = ["jutjat"];
        var mensaje = new Mensaje(Guid.NewGuid(), DireccionMensaje.Entrante, CanalConversacion.Correo, "empresa@example.test", "Denuncia", DateTime.UtcNow);
        await Crear().ProcesarAsync(mensaje, default);
        _repositorio.Alertas.Should().BeEmpty();
        _opciones.CurrentValue.ExpresionesPorOperadorCae[_tenant] = ["denuncia"];
        await Crear().ProcesarAsync(mensaje, default);
        _repositorio.Alertas.Should().ContainSingle();
    }

    [Theory]
    [InlineData("DireccionCae", true)]
    [InlineData("CoordinadorCae", true)]
    [InlineData("Administrador", false)]
    [InlineData("GestorCae", false)]
    [InlineData("Consulta", false)]
    [InlineData("Cliente", false)]
    [InlineData(null, false)]
    public async Task Solo_la_Direccion_Cae_y_el_Coordinador_Cae_pueden_decidir(string? rol, bool permitido)
    {
        var persona = Guid.NewGuid();
        var acceso = new AccesoAlertaCritica(new CurrentUserServiceFalso(persona, rol, _tenant),
            new Sesion(), new Tenant(_tenant), null!, null!, new DirectorioUsuariosServiceFalso());
        (await acceso.PersonaQuePuedeResolverAsync(default)).Should().Be(permitido ? persona : null);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task Se_rechazan_sesiones_privilegiadas_cuentas_desactivadas_y_otro_Tenant(bool privilegiada, bool desactivada, bool otroTenant)
    {
        var acceso = new AccesoAlertaCritica(new CurrentUserServiceFalso(Guid.NewGuid(), "DireccionCae", otroTenant ? Guid.NewGuid() : _tenant),
            new Sesion(privilegiada), new Tenant(_tenant), null!, null!, new DirectorioUsuariosServiceFalso(cuentaActivaConRol: !desactivada));
        (await acceso.PersonaQuePuedeResolverAsync(default)).Should().BeNull();
    }

    private readonly ComunicacionesQueryContextFalso _conversaciones = new();
    private readonly IncidenciaRepositorioFalso _incidencias = new();
    private readonly UnitOfWorkFalso _unidad = new();

    private AccesoAlertaCritica Acceso(Guid persona) => new(new CurrentUserServiceFalso(persona, "DireccionCae", _tenant), new Sesion(),
        new Tenant(_tenant), _conversaciones, new AlcanceDatosServiceFalso(), new DirectorioUsuariosServiceFalso());

    private ResolverAlertaCriticaCommandHandler Resolver(Guid persona) =>
        new(_repositorio, Acceso(persona), Crear(), _incidencias, new AlcanceDatosServiceFalso(), _unidad);

    private DeshacerDerivacionAlertaCriticaCommandHandler Deshacer(Guid persona) =>
        new(_repositorio, Acceso(persona), _incidencias, new AlcanceDatosServiceFalso(), _avisos, _unidad);

    /// <summary>Alerta Pendiente de una conversación nueva, confirmada y derivada por la Dirección CAE con una Incidencia registrada.</summary>
    private async Task<(Conversacion Conversacion, AlertaCriticaConversacion Alerta, Incidencia Incidencia)> DerivadaConIncidenciaAsync()
    {
        var conversacion = new Conversacion("Requerimiento");
        _conversaciones.ListaConversaciones.Add(conversacion);
        await Crear().ProcesarAsync(conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo,
            "empresa@example.test", "Inspección de Trabajo", DateTime.UtcNow), default);
        var alerta = _repositorio.Alertas.Single();
        var incidencia = new Incidencia(Guid.NewGuid(), null, CaeManager.Domain.Incidencias.TipoIncidencia.Incumplimiento,
            CaeManager.Domain.Incidencias.GravedadIncidencia.Grave, DateOnly.FromDateTime(DateTime.UtcNow), "Inspección de Trabajo");
        _incidencias.Agregar(incidencia);
        (await Resolver(_direccion).Handle(new(conversacion.Id, alerta.Id, true, incidencia.Id), default)).EsExitoso.Should().BeTrue();
        return (conversacion, alerta, incidencia);
    }

    [Fact]
    public async Task Deshacer_devuelve_la_alerta_a_Pendiente_elimina_la_Incidencia_y_retira_solo_los_avisos_derivado()
    {
        var (conversacion, alerta, incidencia) = await DerivadaConIncidenciaAsync();
        alerta.IncidenciaId.Should().Be(incidencia.Id);
        var derivados = _avisos.Notificaciones.Where(n => n.EventoOrigenId == incidencia.Id).ToList();
        derivados.Select(n => n.UsuarioDestinatarioId).Should().BeEquivalentTo([_direccion, _coordinador]);
        var guardadosAntes = _unidad.VecesGuardado;

        (await Deshacer(_direccion).Handle(new(conversacion.Id, alerta.Id, incidencia.Id), default)).EsExitoso.Should().BeTrue();

        alerta.Estado.Should().Be(EstadoAlertaCritica.Pendiente);
        alerta.IncidenciaId.Should().BeNull();
        alerta.DecisionPorUsuarioId.Should().BeNull();
        incidencia.EstaEliminado.Should().BeTrue();
        derivados.Should().OnlyContain(n => n.Leida);
        _avisos.Notificaciones.Where(n => n.EventoOrigenId is null).Should().HaveCount(2).And.OnlyContain(n => !n.Leida);
        _unidad.VecesGuardado.Should().Be(guardadosAntes + 1);
    }

    [Fact]
    public async Task Deshacer_no_cambia_nada_si_entre_tanto_llego_otra_alerta_Pendiente_a_la_conversacion()
    {
        var (conversacion, alerta, incidencia) = await DerivadaConIncidenciaAsync();
        await Crear().ProcesarAsync(conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo,
            "empresa@example.test", "Denuncia", DateTime.UtcNow), default);
        _repositorio.Alertas.Should().HaveCount(2);
        var guardadosAntes = _unidad.VecesGuardado;

        var resultado = await Deshacer(_direccion).Handle(new(conversacion.Id, alerta.Id, incidencia.Id), default);

        resultado.Error.Codigo.Should().Be("AlertaCritica.OtraPendiente");
        alerta.Estado.Should().Be(EstadoAlertaCritica.ConfirmadaYDerivada);
        alerta.IncidenciaId.Should().Be(incidencia.Id);
        incidencia.EstaEliminado.Should().BeFalse();
        _avisos.Notificaciones.Where(n => n.EventoOrigenId == incidencia.Id).Should().OnlyContain(n => !n.Leida);
        _unidad.VecesGuardado.Should().Be(guardadosAntes);
    }

    [Fact]
    public async Task Solo_quien_derivo_y_con_su_Incidencia_puede_deshacer()
    {
        var (conversacion, alerta, incidencia) = await DerivadaConIncidenciaAsync();

        (await Deshacer(_coordinador).Handle(new(conversacion.Id, alerta.Id, incidencia.Id), default)).Error.Codigo.Should().Be("AlertaCritica.NoDeshacible");
        (await Deshacer(_direccion).Handle(new(conversacion.Id, alerta.Id, Guid.NewGuid()), default)).Error.Codigo.Should().Be("AlertaCritica.NoDeshacible");
        (await Deshacer(_direccion).Handle(new(Guid.NewGuid(), alerta.Id, incidencia.Id), default)).EsFallido.Should().BeTrue();
        // La Incidencia fuera del alcance de gestión de quien deshace tampoco se elimina.
        var sinCentro = new DeshacerDerivacionAlertaCriticaCommandHandler(_repositorio, Acceso(_direccion), _incidencias,
            new AlcanceDatosServiceFalso(centroIdsParaGestion: []), _avisos, _unidad);
        (await sinCentro.Handle(new(conversacion.Id, alerta.Id, incidencia.Id), default)).Error.Codigo.Should().Be("AlertaCritica.IncidenciaNoDisponible");

        alerta.Estado.Should().Be(EstadoAlertaCritica.ConfirmadaYDerivada);
        incidencia.EstaEliminado.Should().BeFalse();
    }

    [Fact]
    public async Task Resolver_solo_actua_sobre_la_alerta_Pendiente_que_se_mostro()
    {
        var conversacion = new Conversacion("Requerimiento");
        _conversaciones.ListaConversaciones.Add(conversacion);
        await Crear().ProcesarAsync(conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo,
            "empresa@example.test", "Denuncia", DateTime.UtcNow), default);
        var mostrada = _repositorio.Alertas.Single();

        (await Resolver(_direccion).Handle(new(conversacion.Id, Guid.NewGuid(), false), default)).Error.Codigo.Should().Be("AlertaCritica.NoPendiente");
        (await Resolver(_direccion).Handle(new(conversacion.Id, mostrada.Id, false, Guid.NewGuid()), default)).Error.Codigo.Should().Be("AlertaCritica.IncidenciaNoDisponible");
        mostrada.Estado.Should().Be(EstadoAlertaCritica.Pendiente);

        (await Resolver(_direccion).Handle(new(conversacion.Id, mostrada.Id, false), default)).EsExitoso.Should().BeTrue();
        mostrada.Estado.Should().Be(EstadoAlertaCritica.Descartada);
    }

    private sealed class Repositorio : IAlertaCriticaRepository
    {
        public List<AlertaCriticaConversacion> Alertas { get; } = [];
        public bool Lanza { get; set; }
        public void Agregar(AlertaCriticaConversacion alerta) => Alertas.Add(alerta);
        public Task<AlertaCriticaConversacion?> ObtenerPendientePorConversacionAsync(Guid id, CancellationToken ct) =>
            Lanza ? throw new InvalidOperationException("Base de datos caída")
                : Task.FromResult(Alertas.FirstOrDefault(a => a.ConversacionId == id && a.Estado == EstadoAlertaCritica.Pendiente));
        public Task<AlertaCriticaConversacion?> ObtenerVigentePorConversacionAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(Alertas.Where(a => a.ConversacionId == id).OrderByDescending(a => a.Estado == EstadoAlertaCritica.Pendiente)
                .ThenByDescending(a => a.DetectadaEnUtc).FirstOrDefault());
        public Task<AlertaCriticaConversacion?> ObtenerPorIdAsync(Guid id, CancellationToken ct) => Task.FromResult(Alertas.FirstOrDefault(a => a.Id == id));
    }
    private sealed class Tenant(Guid id) : ITenantActual { public Guid? TenantId => id; }
    private sealed class Opinion : ISegundaOpinionCriticaIa
    {
        public bool Falla { get; set; }
        public int Llamadas { get; private set; }
        public Task<SegundaOpinionCritica?> EvaluarAsync(string texto, CancellationToken ct)
        {
            Llamadas++;
            if (Falla) throw new HttpRequestException();
            return Task.FromResult<SegundaOpinionCritica?>(new(false, 81));
        }
    }
    private sealed class Opciones : IOptionsMonitor<AlertasCriticasOptions>
    {
        public AlertasCriticasOptions CurrentValue { get; } = new();
        public AlertasCriticasOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AlertasCriticasOptions, string?> listener) => null;
    }
    private sealed class Sesion(bool activa = false) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken ct = default) => RevalidarAsync(ct);
        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken ct = default) => Task.FromResult<SesionPrivilegiadaActiva?>(
            activa ? new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CapacidadPrivilegio.Impersonacion, null) : null);
    }
    private sealed class Directorio(Guid direccion, Guid coordinador) : IDirectorioUsuariosService
    {
        public Task<Guid?> ObtenerTenantDeUsuarioAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Guid?>(null);
        public Task<bool> EsVisibleEnTenantActualAsync(Guid id, CancellationToken ct = default) => Task.FromResult(true);
        public Task<IReadOnlyDictionary<Guid, string>> ObtenerNombresVisiblesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
        public Task<bool> EsCuentaActivaConRolAsync(Guid id, Guid tenant, string rol, CancellationToken ct = default) => Task.FromResult(true);
        public Task<IReadOnlyList<Guid>> ObtenerCuentasActivasConRolAsync(Guid tenant, string rol, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Guid>>(rol switch
        {
            "DireccionCae" => [direccion],
            "CoordinadorCae" => [coordinador],
            _ => throw new InvalidOperationException("No se debe avisar a otros perfiles.")
        });
    }
}
