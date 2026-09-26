using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Usuarios;
using CaeManager.Application.Usuarios.Commands.GenerarCodigosRecuperacion;
using CaeManager.Application.Usuarios.Commands.RestablecerSegundoFactor;
using CaeManager.Application.Usuarios.Queries.ObtenerRestablecimientoPorSoporte;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
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

    // ---------- Restablecer por Soporte TALVEG (ADR-011 § 8.7, punto 3) ----------

    [Fact]
    public async Task Soporte_con_la_capacidad_restablece_al_Administrador_unico_por_la_funcion_de_la_base()
    {
        var puerto = PuertoConCuentas();
        puerto.AdministradoresUnicos.Add(UsuarioA);
        var sesion = SesionDeSoporte(CapacidadPrivilegio.RestablecimientoSegundoFactor);

        var resultado = await RestablecerPorSesion(puerto, sesion, TenantA)
            .Handle(new RestablecerSegundoFactorCommand(UsuarioA), default);

        resultado.EsExitoso.Should().BeTrue();
        puerto.RestablecidosPorSoporte.Should().Equal((sesion.SesionId, UsuarioA));
        puerto.Restablecidos.Should().BeEmpty("el camino de Identity escribe con el rol de runtime, que esta sesión no tiene");
    }

    [Theory]
    [InlineData(CapacidadPrivilegio.SoporteLectura)]
    [InlineData(CapacidadPrivilegio.Aprovisionamiento)]
    [InlineData(CapacidadPrivilegio.BreakGlass)]
    [InlineData(CapacidadPrivilegio.Impersonacion)]
    [InlineData(CapacidadPrivilegio.AdminPlataforma)]
    public async Task Otra_capacidad_no_basta(CapacidadPrivilegio capacidad)
    {
        var puerto = PuertoConCuentas();
        puerto.AdministradoresUnicos.Add(UsuarioA);

        var resultado = await RestablecerPorSesion(puerto, SesionDeSoporte(capacidad), TenantA)
            .Handle(new RestablecerSegundoFactorCommand(UsuarioA), default);

        resultado.Error.Codigo.Should().Be("SegundoFactor.SesionSinCapacidad");
        NadaEscrito(puerto);
    }

    [Fact]
    public async Task Soporte_no_restablece_simulando_a_otro_usuario()
    {
        var puerto = PuertoConCuentas();
        puerto.AdministradoresUnicos.Add(UsuarioA);
        var sesion = SesionDeSoporte(CapacidadPrivilegio.RestablecimientoSegundoFactor) with { UsuarioSimuladoId = AdminA };

        var resultado = await RestablecerPorSesion(puerto, sesion, TenantA)
            .Handle(new RestablecerSegundoFactorCommand(UsuarioA), default);

        resultado.Error.Codigo.Should().Be("SegundoFactor.ActorNoTitular");
        NadaEscrito(puerto);
    }

    [Fact]
    public async Task Soporte_no_actua_fuera_del_Tenant_objetivo_de_la_sesion()
    {
        var puerto = PuertoConCuentas();
        puerto.AdministradoresUnicos.Add(UsuarioA);

        var resultado = await RestablecerPorSesion(
                puerto, SesionDeSoporte(CapacidadPrivilegio.RestablecimientoSegundoFactor), TenantB)
            .Handle(new RestablecerSegundoFactorCommand(UsuarioA), default);

        resultado.Error.Codigo.Should().Be("SegundoFactor.FueraDelTenantObjetivo");
        NadaEscrito(puerto);
    }

    [Fact]
    public async Task Soporte_no_alcanza_una_cuenta_de_otro_Tenant_ni_una_inexistente()
    {
        var puerto = PuertoConCuentas();
        puerto.AdministradoresUnicos.Add(UsuarioB);
        var sesion = SesionDeSoporte(CapacidadPrivilegio.RestablecimientoSegundoFactor);

        var ajena = await RestablecerPorSesion(puerto, sesion, TenantA)
            .Handle(new RestablecerSegundoFactorCommand(UsuarioB), default);
        var inexistente = await RestablecerPorSesion(puerto, sesion, TenantA)
            .Handle(new RestablecerSegundoFactorCommand(Guid.NewGuid()), default);

        ajena.Error.Codigo.Should().Be("SegundoFactor.CuentaNoEncontrada");
        inexistente.Error.Should().Be(ajena.Error);
        NadaEscrito(puerto);
    }

    [Fact]
    public async Task Soporte_solo_restablece_al_Administrador_unico()
    {
        var puerto = PuertoConCuentas();

        var resultado = await RestablecerPorSesion(
                puerto, SesionDeSoporte(CapacidadPrivilegio.RestablecimientoSegundoFactor), TenantA)
            .Handle(new RestablecerSegundoFactorCommand(UsuarioA), default);

        resultado.Error.Codigo.Should().Be("SegundoFactor.NoEsAdministradorUnico");
        NadaEscrito(puerto);
    }

    [Fact]
    public async Task Soporte_tampoco_restablece_una_cuenta_sin_2FA_activa()
    {
        var puerto = PuertoConCuentas();
        puerto[UsuarioA] = new(TenantA, false, 0);
        puerto.AdministradoresUnicos.Add(UsuarioA);

        var resultado = await RestablecerPorSesion(
                puerto, SesionDeSoporte(CapacidadPrivilegio.RestablecimientoSegundoFactor), TenantA)
            .Handle(new RestablecerSegundoFactorCommand(UsuarioA), default);

        resultado.Error.Codigo.Should().Be("SegundoFactor.NoActivo");
        NadaEscrito(puerto);
    }

    [Fact]
    public async Task Dentro_de_una_sesion_nunca_cae_al_camino_del_Administrador()
    {
        // El técnico con una sesión sin la capacidad, aunque el actor resuelto
        // tuviera rol Administrador, no llega al camino de Identity.
        var puerto = PuertoConCuentas();
        var handler = new RestablecerSegundoFactorCommandHandler(
            Compuesta(puerto, SesionDeSoporte(CapacidadPrivilegio.SoporteLectura), TenantA,
                AdminA, "Administrador", TenantA),
            puerto);

        var resultado = await handler.Handle(new RestablecerSegundoFactorCommand(UsuarioA), default);

        resultado.Error.Codigo.Should().Be("SegundoFactor.SesionSinCapacidad");
        NadaEscrito(puerto);
    }

    [Fact]
    public async Task Sin_sesion_la_compuesta_usa_el_camino_del_Administrador()
    {
        var puerto = PuertoConCuentas();
        var handler = new RestablecerSegundoFactorCommandHandler(
            Compuesta(puerto, null, TenantA, AdminA, "Administrador", TenantA),
            puerto);

        var resultado = await handler.Handle(new RestablecerSegundoFactorCommand(UsuarioA), default);

        resultado.EsExitoso.Should().BeTrue();
        puerto.Restablecidos.Should().Equal(UsuarioA);
        puerto.RestablecidosPorSoporte.Should().BeEmpty();
    }

    // ---------- Lo que ve la pantalla de Soporte TALVEG ----------

    [Fact]
    public async Task La_pantalla_de_Soporte_recibe_al_Administrador_unico_del_Tenant_objetivo()
    {
        var puerto = PuertoConCuentas();
        puerto.AdministradoresUnicos.Add(UsuarioA);
        var handler = new ObtenerRestablecimientoPorSoporteQueryHandler(
            new SesionFija(SesionDeSoporte(CapacidadPrivilegio.RestablecimientoSegundoFactor)), new TenantFijo(TenantA), puerto);

        var resultado = await handler.Handle(new ObtenerRestablecimientoPorSoporteQuery(), default);

        resultado.Should().NotBeNull();
        resultado!.Administrador!.UsuarioId.Should().Be(UsuarioA);
    }

    [Fact]
    public async Task Sin_Administrador_unico_la_seccion_existe_pero_sin_nadie_a_quien_restablecer()
    {
        var handler = new ObtenerRestablecimientoPorSoporteQueryHandler(
            new SesionFija(SesionDeSoporte(CapacidadPrivilegio.RestablecimientoSegundoFactor)), new TenantFijo(TenantA),
            PuertoConCuentas());

        var resultado = await handler.Handle(new ObtenerRestablecimientoPorSoporteQuery(), default);

        resultado.Should().Be(new RestablecimientoPorSoporteDto(null));
    }

    [Theory]
    [InlineData("sin-sesion")]
    [InlineData("otra-capacidad")]
    [InlineData("simulacion")]
    [InlineData("otro-tenant")]
    public async Task Fuera_de_una_sesion_con_la_capacidad_la_seccion_no_existe(string caso)
    {
        var puerto = PuertoConCuentas();
        puerto.AdministradoresUnicos.Add(UsuarioA);
        var conCapacidad = SesionDeSoporte(CapacidadPrivilegio.RestablecimientoSegundoFactor);
        SesionPrivilegiadaActiva? sesion = caso switch
        {
            "sin-sesion" => null,
            "otra-capacidad" => SesionDeSoporte(CapacidadPrivilegio.SoporteLectura),
            "simulacion" => conCapacidad with { UsuarioSimuladoId = AdminA },
            _ => conCapacidad,
        };
        var handler = new ObtenerRestablecimientoPorSoporteQueryHandler(
            new SesionFija(sesion), new TenantFijo(caso == "otro-tenant" ? TenantB : TenantA), puerto);

        (await handler.Handle(new ObtenerRestablecimientoPorSoporteQuery(), default)).Should().BeNull();
    }

    private static SesionPrivilegiadaActiva SesionDeSoporte(CapacidadPrivilegio capacidad) =>
        new(Guid.NewGuid(), Guid.NewGuid(), TenantA, capacidad, null);

    private static void NadaEscrito(SegundoFactorFalso puerto)
    {
        puerto.Restablecidos.Should().BeEmpty();
        puerto.RestablecidosPorSoporte.Should().BeEmpty();
    }

    private static AutorizacionRestablecerSegundoFactorCompuesta Compuesta(
        SegundoFactorFalso puerto, SesionPrivilegiadaActiva? sesion, Guid? tenantOperado,
        Guid actor, string? rol, Guid? tenantOrigen) =>
        new(new SesionFija(sesion),
            new AutorizacionRestablecerSegundoFactorAdministrador(
                new CurrentUserServiceFalso(actor, rol, tenantOrigen), new ActorFijo(ActorAuditoria.Normal(actor)),
                new TenantFijo(tenantOperado), puerto),
            new AutorizacionRestablecerSegundoFactorPorSoporte(new TenantFijo(tenantOperado), puerto));

    // El técnico de Soporte TALVEG no es usuario del Tenant: sin rol efectivo.
    private static RestablecerSegundoFactorCommandHandler RestablecerPorSesion(
        SegundoFactorFalso puerto, SesionPrivilegiadaActiva sesion, Guid? tenantOperado) =>
        new(Compuesta(puerto, sesion, tenantOperado, Guid.NewGuid(), null, null), puerto);

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

        public HashSet<Guid> AdministradoresUnicos { get; } = [];
        public List<(Guid Sesion, Guid Usuario)> RestablecidosPorSoporte { get; } = [];

        public Task<bool> EsAdministradorUnicoActivoAsync(
            Guid usuarioId, Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(AdministradoresUnicos.Contains(usuarioId)
                            && _cuentas.GetValueOrDefault(usuarioId)?.TenantId == tenantId);

        public Task<AdministradorUnicoActivo?> ObtenerAdministradorUnicoActivoAsync(
            Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(AdministradoresUnicos
                .Where(id => _cuentas.GetValueOrDefault(id)?.TenantId == tenantId)
                .Select(id => new AdministradorUnicoActivo(id, "Administradora", "admin@a.test", _cuentas[id].Activo))
                .FirstOrDefault());

        public Task<Result> RestablecerPorSesionPrivilegiadaAsync(
            Guid sesionPrivilegiadaId, Guid usuarioId, CancellationToken cancellationToken = default)
        {
            RestablecidosPorSoporte.Add((sesionPrivilegiadaId, usuarioId));
            return Task.FromResult(Result.Exito());
        }
    }

    private sealed class SesionFija(SesionPrivilegiadaActiva? sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sesion);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sesion);
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
