using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.AlertasCriticas;
using CaeManager.Application.Comunicaciones.Deteccion;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tests.Notificaciones;
using CaeManager.Domain.Comunicaciones;
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

    private AlertaCriticaService Crear() => new(_repositorio, _avisos, new Directorio(_direccion, _coordinador),
        new Tenant(_tenant), _opciones, _opinion, NullLogger<AlertaCriticaService>.Instance);

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

    private sealed class Repositorio : IAlertaCriticaRepository
    {
        public List<AlertaCriticaConversacion> Alertas { get; } = [];
        public void Agregar(AlertaCriticaConversacion alerta) => Alertas.Add(alerta);
        public Task<AlertaCriticaConversacion?> ObtenerPorConversacionAsync(Guid id, CancellationToken ct) => Task.FromResult(Alertas.FirstOrDefault(a => a.ConversacionId == id));
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
            "DireccionCae" => [direccion], "CoordinadorCae" => [coordinador], _ => throw new InvalidOperationException("No se debe avisar a otros perfiles.")
        });
    }
}
