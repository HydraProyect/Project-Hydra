using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento.Commands.ActivarTratamientoIaPorEncargo;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Cumplimiento;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Cumplimiento;

/// <summary>
/// Decisión del 2026-10-15: en un Tenant propietario sin Administrador propio, el Administrador
/// del Operador CAE externo con Encargo de administración pleno activa la IA de ese Tenant, Tenant
/// a Tenant. La instrucción guarda la versión de los textos aceptados, el encargo y su cláusula,
/// y al Actor real; el Tenant propietario es el activo.
/// </summary>
public class ActivarTratamientoIaPorEncargoCommandHandlerTests
{
    private static readonly DateTime Ahora = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);
    private const string Clausula = "Cláusula 7.2 del contrato de servicios";

    private static readonly Guid Propietario = Guid.NewGuid();
    private static readonly Guid Operador = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private readonly List<EncargoAdministracion> _encargos = [];
    private readonly InstruccionTratamientoIaTenantPropietarioRepositoryFalso _repositorio = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();
    private readonly EncargoAdministracion _encargo;
    private readonly AsignacionOperacion _operacion;

    public ActivarTratamientoIaPorEncargoCommandHandlerTests()
    {
        _operacion = AsignacionOperacion.Externa(
            Propietario, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, Ahora.AddDays(-30), null, Ahora.AddDays(-30));
        _encargo = EncargoAdministracion.Registrar(
            _operacion, Clausula, EncargoAdministracion.VersionTextoVigente,
            OrigenEncargoAdministracion.AdministradorPropio, Guid.NewGuid(), Ahora.AddDays(-5), null);
        _encargos.Add(_encargo);
    }

    /// <summary>
    /// Sustituye el encargo de la operación por otro con la vigencia dada y devuelve su Id: la señal
    /// de encargo pleno lo nombra, pero la fila no está vigente en <see cref="Ahora"/>.
    /// </summary>
    private Guid SoloElEncargo(DateTime vigenciaDesde, DateTime? vigenciaHasta)
    {
        var encargo = EncargoAdministracion.Registrar(
            _operacion, Clausula, EncargoAdministracion.VersionTextoVigente,
            OrigenEncargoAdministracion.AdministradorPropio, Guid.NewGuid(), vigenciaDesde, vigenciaHasta);
        _encargos.Clear();
        _encargos.Add(encargo);
        return encargo.Id;
    }

    private sealed record Contexto(
        Guid? TenantActivo, Guid? EncargoPleno, string? RolEfectivo = "Administrador",
        Guid? UsuarioSimulado = null, bool SesionPrivilegiada = false, Guid? UsuarioDeSesion = null);

    private Contexto PorEncargoPleno() => new(Propietario, _encargo.Id);

    private Task<CaeManager.Domain.Common.Result<Guid>> ActivarAsync(Contexto c) =>
        new ActivarTratamientoIaPorEncargoCommandHandler(
                new TenantFijo(c.TenantActivo),
                new CurrentUserServiceFalso(c.UsuarioDeSesion ?? _actor, c.RolEfectivo, Operador),
                new ActorFijo(new ActorAuditoria(_actor, c.UsuarioSimulado, TipoViaAcceso.Normal, null)),
                new SesionFija(c.SesionPrivilegiada
                    ? new SesionPrivilegiadaActiva(Guid.NewGuid(), Guid.NewGuid(), Propietario, CapacidadPrivilegio.Aprovisionamiento, null)
                    : null),
                new EncargoFijo(c.EncargoPleno),
                new EncargosFalsos(_encargos),
                _repositorio, _unitOfWork, new RelojFijo(Ahora))
            .Handle(new ActivarTratamientoIaPorEncargoCommand("DPA-2026-10", "Anexo-II-2026-10"), default);

    [Fact]
    public async Task Con_encargo_pleno_el_Administrador_del_Operador_CAE_activa_la_IA_y_queda_la_traza()
    {
        var resultado = await ActivarAsync(PorEncargoPleno());

        resultado.EsExitoso.Should().BeTrue();
        var instruccion = _repositorio.Filas.Should().ContainSingle().Subject;
        instruccion.Id.Should().Be(resultado.Valor);
        instruccion.OrigenInstruccion.Should().Be(OrigenInstruccionTratamientoIa.EncargoAdministracion);
        instruccion.EncargoAdministracionId.Should().Be(_encargo.Id);
        instruccion.ClausulaContrato.Should().Be(Clausula, "la cláusula se relee del encargo, no la aporta quien actúa");
        instruccion.VersionDpaAceptada.Should().Be("DPA-2026-10");
        instruccion.VersionAnexoSubencargadosAceptada.Should().Be("Anexo-II-2026-10");
        instruccion.RegistradaPorUsuarioId.Should().Be(_actor, "la auditoría guarda al Actor real del Operador CAE");
        instruccion.FechaAceptacionUtc.Should().Be(Ahora);
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    public static TheoryData<string> ContextosQueNoActivan => new()
    {
        "sin Tenant activo", "encargo no pleno", "rol Dirección CAE", "acto simulado",
        "Sesión Privilegiada", "sesión de otro usuario", "encargo de otro Tenant", "encargo retirado",
        "encargo aún no vigente", "encargo caducado",
    };

    [Theory]
    [MemberData(nameof(ContextosQueNoActivan))]
    public async Task Sin_encargo_pleno_vigente_del_Tenant_activo_como_Administrador_real_no_se_activa(string caso)
    {
        var contexto = PorEncargoPleno();
        switch (caso)
        {
            case "sin Tenant activo": contexto = contexto with { TenantActivo = null }; break;
            case "encargo no pleno": contexto = contexto with { EncargoPleno = null }; break;
            case "rol Dirección CAE": contexto = contexto with { RolEfectivo = "DireccionCae" }; break;
            case "acto simulado": contexto = contexto with { UsuarioSimulado = Guid.NewGuid() }; break;
            case "Sesión Privilegiada": contexto = contexto with { SesionPrivilegiada = true }; break;
            case "sesión de otro usuario": contexto = contexto with { UsuarioDeSesion = Guid.NewGuid() }; break;
            case "encargo de otro Tenant": contexto = contexto with { TenantActivo = Guid.NewGuid() }; break;
            case "encargo retirado": _encargo.Retirar(Guid.NewGuid(), Ahora.AddMinutes(-1)); break;
            case "encargo aún no vigente":
                contexto = contexto with { EncargoPleno = SoloElEncargo(Ahora.AddDays(1), null) }; break;
            case "encargo caducado":
                contexto = contexto with { EncargoPleno = SoloElEncargo(Ahora.AddDays(-5), Ahora.AddMinutes(-1)) }; break;
            default: throw new ArgumentOutOfRangeException(nameof(caso));
        }

        (await ActivarAsync(contexto)).Error.Should().Be(ActivarTratamientoIaPorEncargoCommandHandler.NoAutorizado, caso);

        _repositorio.Filas.Should().BeEmpty();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Con_la_IA_ya_activada_no_se_duplica_la_instruccion()
    {
        (await ActivarAsync(PorEncargoPleno())).EsExitoso.Should().BeTrue();

        (await ActivarAsync(PorEncargoPleno())).Error.Should().Be(ActivarTratamientoIaPorEncargoCommandHandler.YaVigente);

        _repositorio.Filas.Should().ContainSingle();
    }

    private sealed class EncargoFijo(Guid? encargoPleno) : IEncargoDeAdministracionActual
    {
        public Task<Guid?> EncargoQueElevaAsync() => Task.FromResult(encargoPleno);

        public Guid? EncargoDeLaUltimaResolucion(Guid asignacionOperacionId) => encargoPleno;

        public Task<Guid?> EncargoPlenoAsync() => Task.FromResult(encargoPleno);

        public bool EncargoPlenoDeLaUltimaResolucion(Guid asignacionOperacionId) => encargoPleno is not null;
    }

    private sealed class EncargosFalsos(List<EncargoAdministracion> encargos) : IEncargosAdministracionQueryContext
    {
        public IQueryable<EncargoAdministracion> EncargosAdministracion =>
            new TestAsyncQueryable<EncargoAdministracion>(encargos.AsQueryable());
    }

    private sealed class SesionFija(SesionPrivilegiadaActiva? sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) => Task.FromResult(sesion);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) => Task.FromResult(sesion);
    }

    private sealed class ActorFijo(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);

        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }

    private sealed class TenantFijo(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    private sealed class RelojFijo(DateTime ahoraUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(ahoraUtc);
    }
}
