using CaeManager.Application.Common;
using CaeManager.Application.Usuarios;
using CaeManager.Application.Usuarios.Commands.GenerarCodigosRecuperacion;
using CaeManager.Application.Usuarios.Commands.RestablecerSegundoFactor;
using CaeManager.Domain.Common;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Usuarios;

/// <summary>
/// P0-8 (hallazgo FS-01): quién puede generar sus códigos de recuperación y quién
/// puede restablecer la 2FA de otra cuenta. La autorización vive en los handlers;
/// aquí se prueba contra un doble del puerto de Identity que registra si se llegó a
/// escribir. La prueba contra PostgreSQL real con el rol de runtime está en
/// <c>SegundoFactorBajoRuntimeTests</c> (IntegrationTests).
/// </summary>
public class SegundoFactorCommandsTests
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private static readonly Guid AdminA = Guid.NewGuid();
    private static readonly Guid UsuarioA = Guid.NewGuid();
    private static readonly Guid UsuarioB = Guid.NewGuid();

    // ---------- Generar códigos (propia cuenta) ----------

    [Fact]
    public async Task Genera_diez_codigos_para_la_propia_cuenta_con_2FA_activa()
    {
        var puerto = new SegundoFactorFalso { [UsuarioA] = new(TenantA, true, 0) };
        var handler = new GenerarCodigosRecuperacionCommandHandler(
            new CurrentUserServiceFalso(UsuarioA, "Consulta", TenantA), new ActorFijo(ActorAuditoria.Normal(UsuarioA)), puerto);

        var resultado = await handler.Handle(new GenerarCodigosRecuperacionCommand(), default);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : "");
        resultado.Valor.Should().HaveCount(10);
        puerto.CodigosGeneradosPara.Should().Equal(UsuarioA);
    }

    [Fact]
    public async Task No_genera_codigos_sin_2FA_activa()
    {
        var puerto = new SegundoFactorFalso { [UsuarioA] = new(TenantA, false, 0) };
        var handler = new GenerarCodigosRecuperacionCommandHandler(
            new CurrentUserServiceFalso(UsuarioA, "GestorCae", TenantA), new ActorFijo(ActorAuditoria.Normal(UsuarioA)), puerto);

        var resultado = await handler.Handle(new GenerarCodigosRecuperacionCommand(), default);

        resultado.Error.Codigo.Should().Be("SegundoFactor.NoActivo");
        puerto.CodigosGeneradosPara.Should().BeEmpty();
    }

    [Fact]
    public async Task Quien_simula_a_otro_usuario_no_obtiene_sus_codigos()
    {
        var puerto = new SegundoFactorFalso { [UsuarioA] = new(TenantA, true, 0) };
        var simulacion = new ActorAuditoria(AdminA, UsuarioA, TipoViaAcceso.SesionPrivilegiada, Guid.NewGuid());
        var handler = new GenerarCodigosRecuperacionCommandHandler(
            new CurrentUserServiceFalso(UsuarioA, "GestorCae", TenantA), new ActorFijo(simulacion), puerto);

        var resultado = await handler.Handle(new GenerarCodigosRecuperacionCommand(), default);

        resultado.Error.Codigo.Should().Be("SegundoFactor.SoloLaPropiaCuenta");
        puerto.CodigosGeneradosPara.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_usuario_resuelto_no_genera_nada()
    {
        var puerto = new SegundoFactorFalso();
        var handler = new GenerarCodigosRecuperacionCommandHandler(
            new CurrentUserServiceFalso(), new ActorFijo(ActorAuditoria.SinResolver), puerto);

        var resultado = await handler.Handle(new GenerarCodigosRecuperacionCommand(), default);

        resultado.EsFallido.Should().BeTrue();
        puerto.CodigosGeneradosPara.Should().BeEmpty();
    }

    // ---------- Restablecer (otra cuenta del mismo Tenant) ----------

    [Fact]
    public async Task Un_Administrador_restablece_la_2FA_de_otra_cuenta_de_su_Tenant()
    {
        var puerto = PuertoConCuentas();
        var handler = Restablecer(puerto, AdminA, "Administrador", tenantOrigen: TenantA, tenantOperado: TenantA);

        var resultado = await handler.Handle(new RestablecerSegundoFactorCommand(UsuarioA), default);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : "");
        puerto.Restablecidos.Should().Equal(UsuarioA);
    }

    [Theory]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    [InlineData(null)]
    public async Task Solo_el_rol_efectivo_Administrador_puede(string? rol)
    {
        var puerto = PuertoConCuentas();
        var handler = Restablecer(puerto, AdminA, rol, TenantA, TenantA);

        var resultado = await handler.Handle(new RestablecerSegundoFactorCommand(UsuarioA), default);

        resultado.Error.Codigo.Should().Be("SegundoFactor.SoloAdministrador");
        puerto.Restablecidos.Should().BeEmpty();
    }

    [Fact]
    public async Task Nunca_entre_Tenants_y_con_el_mismo_mensaje_que_una_cuenta_inexistente()
    {
        var puerto = PuertoConCuentas();
        var handler = Restablecer(puerto, AdminA, "Administrador", TenantA, TenantA);

        var ajena = await handler.Handle(new RestablecerSegundoFactorCommand(UsuarioB), default);
        var inexistente = await handler.Handle(new RestablecerSegundoFactorCommand(Guid.NewGuid()), default);

        ajena.Error.Codigo.Should().Be("SegundoFactor.CuentaNoEncontrada");
        ajena.Error.Mensaje.Should().Be(inexistente.Error.Mensaje, "otro mensaje revelaría que el Id es de otra organización");
        puerto.Restablecidos.Should().BeEmpty();
    }

    [Fact]
    public async Task Operando_un_Tenant_que_no_es_el_de_origen_no_puede()
    {
        // Aunque el rol efectivo dijera Administrador (la Operación no lo concede,
        // pero el handler no se apoya en eso), el Tenant operado tiene que ser el
        // Tenant propietario de la cuenta del actor.
        var puerto = PuertoConCuentas();
        var handler = Restablecer(puerto, AdminA, "Administrador", tenantOrigen: TenantA, tenantOperado: TenantB);

        var resultado = await handler.Handle(new RestablecerSegundoFactorCommand(UsuarioB), default);

        resultado.Error.Codigo.Should().Be("SegundoFactor.FueraDelTenantPropietario");
        puerto.Restablecidos.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_Tenant_operado_falla_cerrado()
    {
        var puerto = PuertoConCuentas();
        var handler = Restablecer(puerto, AdminA, "Administrador", TenantA, tenantOperado: null);

        var resultado = await handler.Handle(new RestablecerSegundoFactorCommand(UsuarioA), default);

        resultado.Error.Codigo.Should().Be("SegundoFactor.FueraDelTenantPropietario");
        puerto.Restablecidos.Should().BeEmpty();
    }

    [Fact]
    public async Task No_sobre_la_propia_cuenta()
    {
        var puerto = PuertoConCuentas();
        var handler = Restablecer(puerto, AdminA, "Administrador", TenantA, TenantA);

        var resultado = await handler.Handle(new RestablecerSegundoFactorCommand(AdminA), default);

        resultado.Error.Codigo.Should().Be("SegundoFactor.PropiaCuenta");
        puerto.Restablecidos.Should().BeEmpty();
    }

    [Fact]
    public async Task Bajo_una_simulacion_de_usuario_no_puede()
    {
        var puerto = PuertoConCuentas();
        var simulacion = new ActorAuditoria(Guid.NewGuid(), AdminA, TipoViaAcceso.SesionPrivilegiada, Guid.NewGuid());
        var handler = new RestablecerSegundoFactorCommandHandler(
            new AutorizacionRestablecerSegundoFactorAdministrador(
                new CurrentUserServiceFalso(AdminA, "Administrador", TenantA), new ActorFijo(simulacion),
                new TenantFijo(TenantA), puerto),
            puerto);

        var resultado = await handler.Handle(new RestablecerSegundoFactorCommand(UsuarioA), default);

        resultado.Error.Codigo.Should().Be("SegundoFactor.ActorNoTitular");
        puerto.Restablecidos.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_cuenta_sin_2FA_activa_no_se_restablece()
    {
        var puerto = PuertoConCuentas();
        puerto[UsuarioA] = new(TenantA, false, 0);
        var handler = Restablecer(puerto, AdminA, "Administrador", TenantA, TenantA);

        var resultado = await handler.Handle(new RestablecerSegundoFactorCommand(UsuarioA), default);

        resultado.Error.Codigo.Should().Be("SegundoFactor.NoActivo");
        puerto.Restablecidos.Should().BeEmpty();
    }

    private static SegundoFactorFalso PuertoConCuentas() => new()
    {
        [AdminA] = new(TenantA, true, 10),
        [UsuarioA] = new(TenantA, true, 7),
        [UsuarioB] = new(TenantB, true, 10),
    };

    private static RestablecerSegundoFactorCommandHandler Restablecer(
        SegundoFactorFalso puerto, Guid actor, string? rol, Guid? tenantOrigen, Guid? tenantOperado) =>
        new(new AutorizacionRestablecerSegundoFactorAdministrador(
                new CurrentUserServiceFalso(actor, rol, tenantOrigen), new ActorFijo(ActorAuditoria.Normal(actor)),
                new TenantFijo(tenantOperado), puerto),
            puerto);

    private sealed class SegundoFactorFalso : ISegundoFactorDeCuentas
    {
        private readonly Dictionary<Guid, EstadoSegundoFactor> _cuentas = [];

        public EstadoSegundoFactor this[Guid id] { set => _cuentas[id] = value; }

        public List<Guid> CodigosGeneradosPara { get; } = [];
        public List<Guid> Restablecidos { get; } = [];

        public Task<EstadoSegundoFactor?> ObtenerEstadoAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_cuentas.GetValueOrDefault(usuarioId));

        public Task<Result<IReadOnlyList<string>>> GenerarCodigosRecuperacionAsync(
            Guid usuarioId, int cantidad, CancellationToken cancellationToken = default)
        {
            CodigosGeneradosPara.Add(usuarioId);
            IReadOnlyList<string> codigos = Enumerable.Range(0, cantidad).Select(i => $"COD{i:00}-XXXXX").ToList();
            return Task.FromResult(Result.Exito(codigos));
        }

        public Task<Result> RestablecerAsync(Guid usuarioId, CancellationToken cancellationToken = default)
        {
            Restablecidos.Add(usuarioId);
            return Task.FromResult(Result.Exito());
        }
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
}
