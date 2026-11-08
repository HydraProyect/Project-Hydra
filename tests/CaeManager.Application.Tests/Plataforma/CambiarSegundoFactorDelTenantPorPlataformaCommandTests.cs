using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Plataforma.Commands.CambiarSegundoFactorDelTenantPorPlataforma;
using CaeManager.Application.Plataforma.Queries.ObtenerAjusteSegundoFactorPorPlataforma;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Plataforma;

/// <summary>
/// La vía de un Actor de Plataforma TALVEG para cambiar la exigencia de segundo factor de un Tenant
/// (decisiones del 2026-11-08; ASVS 4.0.3 V4.1.3, V4.2.1, V7.1.3). Aquí, las reglas de Application:
/// sesión, capacidad, simulación, Tenant objetivo, Tenant de plataforma y motivo. Que la base no
/// dependa de ellas lo prueba <c>PlataformaAjustaSegundoFactorTenantBajoRuntimeTests</c>.
/// </summary>
public class CambiarSegundoFactorDelTenantPorPlataformaCommandTests
{
    private const string Motivo = "Petición del Tenant, ticket SOP-2048";

    private readonly Tenant _tenant = new("Tenant objetivo S.L.");
    private readonly TenantsQueryContextFalso _tenants = new();
    private readonly AjusteFalso _puerta = new();

    public CambiarSegundoFactorDelTenantPorPlataformaCommandTests() => _tenants.ListaTenants.Add(_tenant);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Con_la_concesion_sobre_el_Tenant_cambia_por_la_puerta_de_la_base_que_audita(bool exigir)
    {
        var sesion = Sesion(CapacidadPrivilegio.AjusteSegundoFactorTenant);

        var resultado = await Handler(sesion, _tenant.Id)
            .Handle(new CambiarSegundoFactorDelTenantPorPlataformaCommand(exigir, "  " + Motivo + "  "), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        _puerta.Llamadas.Should().ContainSingle()
            .Which.Should().Be((sesion.SesionId, exigir, Motivo),
                "la sesión que ampara el acto viaja a la base, que escribe y audita con el motivo");
    }

    [Fact]
    public async Task Sin_Sesion_Privilegiada_falla_aunque_el_rol_escriba()
    {
        // Es el caso del Administrador del Tenant: AutorizacionEscrituraBehavior deja pasar su rol
        // de escritura, y este handler no. Su vía es ActualizarSeguridadAccesoCommand.
        var resultado = await Handler(null, _tenant.Id)
            .Handle(new CambiarSegundoFactorDelTenantPorPlataformaCommand(true, Motivo), CancellationToken.None);

        resultado.Error!.Codigo.Should().Be("SeguridadAcceso.SoloPlataformaConSesionPrivilegiada");
        _puerta.Llamadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Con_la_concesion_revocada_falla_aunque_la_memo_conserve_la_sesion()
    {
        var resultado = await new CambiarSegundoFactorDelTenantPorPlataformaCommandHandler(
                new SesionMemoizadaRevocada(Sesion(CapacidadPrivilegio.AjusteSegundoFactorTenant)),
                new TenantActualFalso(_tenant.Id), _tenants, _puerta)
            .Handle(new CambiarSegundoFactorDelTenantPorPlataformaCommand(true, Motivo), CancellationToken.None);

        resultado.Error!.Codigo.Should().Be("SeguridadAcceso.SoloPlataformaConSesionPrivilegiada");
        _puerta.Llamadas.Should().BeEmpty();
    }

    [Theory]
    [InlineData(CapacidadPrivilegio.RestablecimientoSegundoFactor)]
    [InlineData(CapacidadPrivilegio.SoporteLectura)]
    [InlineData(CapacidadPrivilegio.Aprovisionamiento)]
    [InlineData(CapacidadPrivilegio.RevocacionOperadorCaeExterno)]
    [InlineData(CapacidadPrivilegio.ConcesionAdministrador)]
    [InlineData(CapacidadPrivilegio.AdminPlataforma)]
    public async Task Una_sesion_sin_esta_capacidad_falla(CapacidadPrivilegio capacidad)
    {
        var resultado = await Handler(Sesion(capacidad), _tenant.Id)
            .Handle(new CambiarSegundoFactorDelTenantPorPlataformaCommand(true, Motivo), CancellationToken.None);

        resultado.Error!.Codigo.Should().Be("SeguridadAcceso.SesionSinCapacidad");
        _puerta.Llamadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Simulando_a_otro_usuario_falla()
    {
        var sesion = Sesion(CapacidadPrivilegio.AjusteSegundoFactorTenant) with { UsuarioSimuladoId = Guid.NewGuid() };

        var resultado = await Handler(sesion, _tenant.Id)
            .Handle(new CambiarSegundoFactorDelTenantPorPlataformaCommand(true, Motivo), CancellationToken.None);

        resultado.Error!.Codigo.Should().Be("SeguridadAcceso.ActorNoTitular");
        _puerta.Llamadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_concesion_sobre_otro_Tenant_falla()
    {
        // La sesión se abrió sobre otro Tenant y el Tenant operado es este: no la ampara.
        var sesion = Sesion(CapacidadPrivilegio.AjusteSegundoFactorTenant) with { TenantObjetivoId = Guid.NewGuid() };

        var resultado = await Handler(sesion, _tenant.Id)
            .Handle(new CambiarSegundoFactorDelTenantPorPlataformaCommand(true, Motivo), CancellationToken.None);

        resultado.Error!.Codigo.Should().Be("SeguridadAcceso.FueraDelTenantObjetivo");
        _puerta.Llamadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Tenant_de_plataforma_como_objetivo_falla()
    {
        var plataforma = new Tenant("TALVEG");
        plataforma.MarcarComoPlataforma();
        _tenants.ListaTenants.Add(plataforma);
        var sesion = Sesion(CapacidadPrivilegio.AjusteSegundoFactorTenant) with { TenantObjetivoId = plataforma.Id };

        var resultado = await Handler(sesion, plataforma.Id)
            .Handle(new CambiarSegundoFactorDelTenantPorPlataformaCommand(false, Motivo), CancellationToken.None);

        resultado.Error!.Codigo.Should().Be("SeguridadAcceso.TenantDePlataforma");
        _puerta.Llamadas.Should().BeEmpty();
    }

    [Fact]
    public void El_plazo_de_inactividad_no_viaja_por_esta_via()
    {
        // Ni el comando ni la puerta de la base llevan el plazo: no hay forma de cambiarlo aquí.
        typeof(CambiarSegundoFactorDelTenantPorPlataformaCommand).GetProperties().Select(p => p.Name)
            .Should().BeEquivalentTo(["ExigeSegundoFactorTodosLosPerfiles", "Motivo"]);
        typeof(IAjusteSegundoFactorPorPlataforma).GetMethods()
            .SelectMany(m => m.GetParameters()).Select(p => p.ParameterType)
            .Should().NotContain(typeof(int), "el plazo de inactividad lo fija solo el Administrador del Tenant");
    }

    [Fact]
    public async Task Lo_que_niega_la_base_llega_tal_cual()
    {
        _puerta.Respuesta = Result.Fallo(Error.Crear("SeguridadAcceso.SinCambios", "La organización ya tiene ese ajuste."));

        var resultado = await Handler(Sesion(CapacidadPrivilegio.AjusteSegundoFactorTenant), _tenant.Id)
            .Handle(new CambiarSegundoFactorDelTenantPorPlataformaCommand(true, Motivo), CancellationToken.None);

        resultado.Error!.Codigo.Should().Be("SeguridadAcceso.SinCambios");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Sin_motivo_no_valida(string? motivo)
    {
        new CambiarSegundoFactorDelTenantPorPlataformaCommandValidator()
            .Validate(new CambiarSegundoFactorDelTenantPorPlataformaCommand(true, motivo!))
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void Un_motivo_de_mas_de_500_caracteres_no_valida()
    {
        var validador = new CambiarSegundoFactorDelTenantPorPlataformaCommandValidator();

        validador.Validate(new CambiarSegundoFactorDelTenantPorPlataformaCommand(true, new string('x', 501)))
            .IsValid.Should().BeFalse();
        validador.Validate(new CambiarSegundoFactorDelTenantPorPlataformaCommand(true, new string('x', 500)))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void Es_el_comando_marcado_de_la_capacidad_y_un_comando()
    {
        var comando = new CambiarSegundoFactorDelTenantPorPlataformaCommand(true, Motivo);

        comando.Should().BeAssignableTo<IComandoDeAjusteSegundoFactorTenant>();
        comando.Should().BeAssignableTo<ICommand>();
        comando.Should().NotBeAssignableTo<IComandoDeAprovisionamiento>();
        comando.Should().NotBeAssignableTo<IComandoDeRestablecimientoSegundoFactor>();
        comando.Should().NotBeAssignableTo<IComandoDeRevocacionOperadorCaeExterno>();
        comando.Should().NotBeAssignableTo<IComandoDeConcesionAdministrador>();
    }

    // ── La lectura que ofrece la sección ─────────────────────────────────────

    [Fact]
    public async Task La_seccion_solo_existe_con_la_sesion_de_la_capacidad_sobre_su_Tenant()
    {
        var parametros = new ParametroSistema(30, 15);
        parametros.ActualizarSeguridadAcceso(45, exigeSegundoFactorTodosLosPerfiles: true);

        (await Consulta(Sesion(CapacidadPrivilegio.AjusteSegundoFactorTenant), _tenant.Id, parametros))
            .Should().Be(new AjusteSegundoFactorPorPlataformaDto(true));
        (await Consulta(null, _tenant.Id, parametros)).Should().BeNull();
        (await Consulta(Sesion(CapacidadPrivilegio.RestablecimientoSegundoFactor), _tenant.Id, parametros)).Should().BeNull();
        (await Consulta(Sesion(CapacidadPrivilegio.AjusteSegundoFactorTenant), Guid.NewGuid(), parametros)).Should().BeNull();
        (await Consulta(Sesion(CapacidadPrivilegio.AjusteSegundoFactorTenant) with { UsuarioSimuladoId = Guid.NewGuid() },
            _tenant.Id, parametros)).Should().BeNull();
    }

    [Fact]
    public async Task Con_la_concesion_revocada_en_otra_pestana_la_seccion_no_existe_aunque_la_memo_la_conserve()
    {
        var parametros = new ParametroSistema(30, 15);
        var sesionMemoizada = new SesionMemoizadaRevocada(Sesion(CapacidadPrivilegio.AjusteSegundoFactorTenant));

        var resultado = await new ObtenerAjusteSegundoFactorPorPlataformaQueryHandler(
                sesionMemoizada, new TenantActualFalso(_tenant.Id), new ConfiguracionFalsa(parametros))
            .Handle(new ObtenerAjusteSegundoFactorPorPlataformaQuery(), CancellationToken.None);

        resultado.Should().BeNull();
    }

    private Task<AjusteSegundoFactorPorPlataformaDto?> Consulta(
        SesionPrivilegiadaActiva? sesion, Guid tenantActivo, ParametroSistema parametros) =>
        new ObtenerAjusteSegundoFactorPorPlataformaQueryHandler(
                new SesionFalsa(sesion), new TenantActualFalso(tenantActivo), new ConfiguracionFalsa(parametros))
            .Handle(new ObtenerAjusteSegundoFactorPorPlataformaQuery(), CancellationToken.None);

    private SesionPrivilegiadaActiva Sesion(CapacidadPrivilegio capacidad) =>
        new(Guid.NewGuid(), Guid.NewGuid(), _tenant.Id, capacidad, null);

    private CambiarSegundoFactorDelTenantPorPlataformaCommandHandler Handler(SesionPrivilegiadaActiva? sesion, Guid? tenantActivo) =>
        new(new SesionFalsa(sesion), new TenantActualFalso(tenantActivo), _tenants, _puerta);

    private sealed class AjusteFalso : IAjusteSegundoFactorPorPlataforma
    {
        public List<(Guid Sesion, bool Exige, string Motivo)> Llamadas { get; } = [];

        public Result Respuesta { get; set; } = Result.Exito();

        public Task<Result> CambiarAsync(
            Guid sesionPrivilegiadaId, bool exigeSegundoFactorTodosLosPerfiles, string motivo,
            CancellationToken cancellationToken)
        {
            Llamadas.Add((sesionPrivilegiadaId, exigeSegundoFactorTodosLosPerfiles, motivo));
            return Task.FromResult(Respuesta);
        }
    }

    private sealed class SesionFalsa(SesionPrivilegiadaActiva? sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sesion);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sesion);
    }

    // La memo del circuito aún conserva la sesión; la base ya no la reconoce porque su
    // concesión se revocó después de la primera resolución.
    private sealed class SesionMemoizadaRevocada(SesionPrivilegiadaActiva memoizada) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(memoizada);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }

    private sealed class TenantActualFalso(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    private sealed class ConfiguracionFalsa(ParametroSistema parametros) : IConfiguracionQueryContext
    {
        public IQueryable<ParametroSistema> ParametrosSistema =>
            new CaeManager.Application.Tests.Integraciones.TestAsyncQueryable<ParametroSistema>(new[] { parametros }.AsQueryable());
        public IQueryable<PreferenciaDashboardUsuario> PreferenciasDashboardUsuario => throw new NotSupportedException();
        public IQueryable<FiltroGuardado> FiltrosGuardados => throw new NotSupportedException();
        public IQueryable<OrdenCajasFicha> OrdenesCajasFicha => throw new NotSupportedException();
        public IQueryable<EstadoAutomatizacion> EstadosAutomatizacion => throw new NotSupportedException();
    }
}
