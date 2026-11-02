using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Commands.FijarResponsableBuzon;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Integraciones;

/// <summary>
/// El responsable del buzón <b>como <c>cae_app_runtime</c></b>, con los interceptores de sellado, de sesión RLS y de
/// concurrencia de producción, y con el directorio, las carteras y el <see cref="AlcanceDatosService"/> reales. Dos
/// Tenants propietarios con un buzón cada uno; la sesión es del primero. Lo que se mide: el buzón ajeno no se lee ni se
/// escribe por Id, la cuenta ajena no se puede nombrar, y la regla de elegibilidad da lo mismo con sesión que en la
/// ingesta, que corre sin usuario y solo con el Tenant del buzón. El control demuestra que, sin filtro global de EF, es
/// RLS quien oculta y bloquea. La siembra va como propietario de la base, porque no es lo que se mide.
/// </summary>
public class FijarResponsableBuzonBajoRlsTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _administrador = Guid.NewGuid();
    private readonly Guid _gestorConCartera = Guid.NewGuid();
    private readonly Guid _gestorSinCartera = Guid.NewGuid();
    private readonly Guid _cuentaDePortal = Guid.NewGuid();
    private readonly Guid _gestorAjeno = Guid.NewGuid();

    private CaeManagerDbContext _propietario = null!;
    private TenantActualDeLaPeticion _tenantDeLaPeticion = null!;
    private Guid _tenantSesion;
    private Guid _tenantAjeno;
    private Guid _buzonPropio;
    private Guid _buzonSinDiscriminador;
    private Guid _buzonAjeno;
    private Guid _carteraPropia;
    private Guid _carteraAjena;

    public async Task InitializeAsync()
    {
        var tenantPorAmbito = new TenantActualPorAmbito();
        var opcionesPropietario = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
            .Options;
        _propietario = new CaeManagerDbContext(opcionesPropietario, new EphemeralDataProtectionProvider(), tenantPorAmbito);
        await _propietario.Database.MigrateAsync();

        var tenantSesion = new Tenant("Tenant propietario de la sesión");
        var tenantAjeno = new Tenant("Tenant propietario ajeno");
        _propietario.Tenants.AddRange(tenantSesion, tenantAjeno);
        await _propietario.SaveChangesAsync();
        _tenantSesion = tenantSesion.Id;
        _tenantAjeno = tenantAjeno.Id;
        _tenantDeLaPeticion = new TenantActualDeLaPeticion(_tenantSesion);

        var roles = await _propietario.Roles.AsNoTracking().ToDictionaryAsync(r => r.Name!, r => r.Id);
        void Cuenta(Guid id, Guid tenantId, string rol)
        {
            _propietario.Users.Add(new ApplicationUser
            {
                Id = id,
                TenantId = tenantId,
                UserName = $"{id:N}@caemanager.local",
                NormalizedUserName = $"{id:N}@CAEMANAGER.LOCAL",
                Email = $"{id:N}@caemanager.local",
                NormalizedEmail = $"{id:N}@CAEMANAGER.LOCAL",
                NombreCompleto = rol,
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString(),
                ConcurrencyStamp = Guid.NewGuid().ToString(),
            });
            _propietario.UserRoles.Add(new IdentityUserRole<Guid> { UserId = id, RoleId = roles[rol] });
        }

        Cuenta(_administrador, _tenantSesion, Roles.Administrador);
        Cuenta(_gestorConCartera, _tenantSesion, Roles.GestorCae);
        Cuenta(_gestorSinCartera, _tenantSesion, Roles.GestorCae);
        Cuenta(_cuentaDePortal, _tenantSesion, Roles.Cliente);
        Cuenta(_gestorAjeno, _tenantAjeno, Roles.GestorCae);
        await _propietario.SaveChangesAsync();

        using (AmbitoTenantExplicito.Establecer(_tenantSesion))
        {
            var empresa = Empresa.CrearComoCliente("Montajes Springfield", "B12345674", false, null, null);
            _propietario.Empresas.Add(empresa);
            await _propietario.SaveChangesAsync();

            _carteraPropia = await SembrarCarteraAsync(_tenantSesion, _gestorConCartera);

            var buzon = new ConexionIntegracion("cae@ejemplo.test", "Buzón CAE", empresa.Id);
            _propietario.ConexionesIntegracion.Add(buzon);
            await _propietario.SaveChangesAsync();
            _buzonPropio = buzon.Id;

            var sinDiscriminador = Empresa.CrearComoSubcontrata("Transportes Shelbyville", null, "Estandar");
            _propietario.Empresas.Add(sinDiscriminador);
            await _propietario.SaveChangesAsync();

            var buzonSinDiscriminador = new ConexionIntegracion("contraparte@ejemplo.test", "Buzón de contraparte", sinDiscriminador.Id);
            _propietario.ConexionesIntegracion.Add(buzonSinDiscriminador);
            await _propietario.SaveChangesAsync();
            _buzonSinDiscriminador = buzonSinDiscriminador.Id;
        }

        using (AmbitoTenantExplicito.Establecer(_tenantAjeno))
        {
            _carteraAjena = await SembrarCarteraAsync(_tenantAjeno, _gestorAjeno);

            var buzon = new ConexionIntegracion("cae@ajeno.test", "Buzón ajeno");
            _propietario.ConexionesIntegracion.Add(buzon);
            await _propietario.SaveChangesAsync();
            _buzonAjeno = buzon.Id;
        }
    }

    public async Task DisposeAsync()
    {
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    /// <summary>Control del instrumento: la conexión está de verdad bajo RLS y el buzón ajeno existe.</summary>
    [Fact]
    public async Task La_conexion_va_bajo_RLS_efectiva_y_el_buzon_ajeno_existe()
    {
        await using var runtime = CrearRuntime(_tenantDeLaPeticion, ActorAdministrador());

        (await runtime.ConexionesIntegracion.IgnoreQueryFilters().CountAsync(c => c.Id == _buzonAjeno)).Should().Be(0,
            "sin filtro global de EF, solo RLS puede ocultar el buzón del Tenant ajeno");
        (await runtime.ConexionesIntegracion.IgnoreQueryFilters().CountAsync(c => c.Id == _buzonPropio)).Should().Be(1,
            "control positivo: el buzón del Tenant de la sesión sí se ve");
        (await _propietario.ConexionesIntegracion.IgnoreQueryFilters().CountAsync(c => c.Id == _buzonAjeno)).Should().Be(1);
    }

    /// <summary>La barrera de escritura medida sin el repositorio ni el filtro global: un UPDATE por Id no toca la fila.</summary>
    [Fact]
    public async Task Un_UPDATE_por_Id_del_buzon_ajeno_no_afecta_a_ninguna_fila()
    {
        await using var runtime = CrearRuntime(_tenantDeLaPeticion, ActorAdministrador());

        (await runtime.ConexionesIntegracion.IgnoreQueryFilters()
                .Where(c => c.Id == _buzonAjeno)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.ResponsableUsuarioId, _gestorConCartera)))
            .Should().Be(0);
        (await runtime.ConexionesIntegracion.IgnoreQueryFilters()
                .Where(c => c.Id == _buzonPropio)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.ResponsableUsuarioId, _gestorConCartera)))
            .Should().Be(1, "control positivo: el mismo UPDATE sobre el buzón propio sí llega a la fila");

        (await LeerResponsableAsync(_buzonAjeno)).Should().BeNull();
    }

    [Fact]
    public async Task Fijar_por_Id_el_responsable_de_un_buzon_de_otro_Tenant_responde_no_encontrada_y_no_lo_cambia()
    {
        await using var runtime = CrearRuntime(_tenantDeLaPeticion, ActorAdministrador());

        var resultado = await Handler(runtime).Handle(
            new FijarResponsableBuzonCommand(_buzonAjeno, _gestorConCartera), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("ConexionIntegracion.NoEncontrada");
        (await LeerResponsableAsync(_buzonAjeno)).Should().BeNull();
    }

    /// <summary>Control positivo de la escritura, con la cartera y la cuenta leídas bajo RLS.</summary>
    [Fact]
    public async Task Un_Gestor_CAE_con_cartera_se_fija_y_se_persiste()
    {
        await using var runtime = CrearRuntime(_tenantDeLaPeticion, ActorAdministrador());

        var resultado = await Handler(runtime).Handle(
            new FijarResponsableBuzonCommand(_buzonPropio, _gestorConCartera), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : null);
        (await LeerResponsableAsync(_buzonPropio)).Should().Be(_gestorConCartera);
    }

    [Fact]
    public async Task Fijar_rechaza_la_cuenta_ajena_el_rol_de_portal_y_al_Gestor_CAE_sin_cartera()
    {
        foreach (var (cuenta, codigo) in new[]
                 {
                     (_gestorAjeno, "Integraciones.Buzon.ResponsableNoValido"),
                     (_cuentaDePortal, "Integraciones.Buzon.ResponsableSinGestion"),
                     (_gestorSinCartera, "Integraciones.Buzon.ResponsableSinCartera"),
                 })
        {
            await using var runtime = CrearRuntime(_tenantDeLaPeticion, ActorAdministrador());

            var resultado = await Handler(runtime).Handle(
                new FijarResponsableBuzonCommand(_buzonPropio, cuenta), CancellationToken.None);

            resultado.Error.Codigo.Should().Be(codigo);
        }

        (await LeerResponsableAsync(_buzonPropio)).Should().BeNull();
    }

    /// <summary>
    /// La ingesta no tiene usuario: la política de las carteras solo se abre por el Tenant propietario. Sin filtro
    /// global, la cartera ajena no se ve y la propia sí, y de ahí salen los cuatro desenlaces.
    /// </summary>
    [Fact]
    public async Task La_ingesta_sin_usuario_evalua_bajo_RLS_solo_con_el_Tenant_del_buzon()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantSesion);
        var tenant = new TenantActualPorAmbito();
        await using var runtime = CrearRuntime(tenant, new CurrentUserServiceFalso());

        (await runtime.AsignacionesCartera.IgnoreQueryFilters().CountAsync(c => c.Id == _carteraAjena)).Should().Be(0);
        (await runtime.AsignacionesCartera.IgnoreQueryFilters().CountAsync(c => c.Id == _carteraPropia)).Should().Be(1);

        var buzon = await runtime.ConexionesIntegracion.SingleAsync(c => c.Id == _buzonPropio);
        var elegibilidad = Elegibilidad(runtime, tenant);

        (await elegibilidad.EvaluarAsync(_gestorConCartera, buzon, CancellationToken.None)).Should().BeNull();
        (await elegibilidad.EvaluarAsync(_gestorSinCartera, buzon, CancellationToken.None))
            .Should().Be(MotivoResponsableNoElegible.SinCarteraSobreLaEmpresa);
        (await elegibilidad.EvaluarAsync(_cuentaDePortal, buzon, CancellationToken.None))
            .Should().Be(MotivoResponsableNoElegible.RolSinGestion);
        (await elegibilidad.EvaluarAsync(_gestorAjeno, buzon, CancellationToken.None))
            .Should().Be(MotivoResponsableNoElegible.NoAlcanzable);
    }

    /// <summary>
    /// El alcance del Tenant entero solo incluye las Empresas con discriminador: con cartera universal y sin él, la
    /// ingesta deja la conversación sin asignar y fijarlo se rechaza. Administrador lo atiende por su rol.
    /// </summary>
    [Fact]
    public async Task Una_Empresa_sin_discriminador_queda_fuera_de_la_cartera_universal_al_ingerir_y_al_fijar()
    {
        using (AmbitoTenantExplicito.Establecer(_tenantSesion))
        {
            var tenant = new TenantActualPorAmbito();
            await using var sinUsuario = CrearRuntime(tenant, new CurrentUserServiceFalso());
            var buzon = await sinUsuario.ConexionesIntegracion.SingleAsync(c => c.Id == _buzonSinDiscriminador);
            var elegibilidad = Elegibilidad(sinUsuario, tenant);

            (await elegibilidad.EvaluarAsync(_gestorConCartera, buzon, CancellationToken.None))
                .Should().Be(MotivoResponsableNoElegible.SinCarteraSobreLaEmpresa);
            (await elegibilidad.EvaluarAsync(_administrador, buzon, CancellationToken.None)).Should().BeNull();
        }

        await using var runtime = CrearRuntime(_tenantDeLaPeticion, ActorAdministrador());
        var resultado = await Handler(runtime).Handle(
            new FijarResponsableBuzonCommand(_buzonSinDiscriminador, _gestorConCartera), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Integraciones.Buzon.ResponsableSinCartera");
        (await LeerResponsableAsync(_buzonSinDiscriminador)).Should().BeNull();
    }

    private async Task<Guid> SembrarCarteraAsync(Guid tenantId, Guid usuarioId)
    {
        var ahora = DateTime.UtcNow;
        var raiz = AsignacionOperacion.Raiz(tenantId, ServicioCae.Outbound, ahora.AddDays(-2), ahora);
        var cartera = AsignacionCartera.Interna(raiz, usuarioId, AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora);
        _propietario.AsignacionesOperacion.Add(raiz);
        _propietario.AsignacionesCartera.Add(cartera);
        await _propietario.SaveChangesAsync();
        return cartera.Id;
    }

    private CurrentUserServiceFalso ActorAdministrador() =>
        new(_administrador, Roles.Administrador, tenantOrigenId: _tenantSesion);

    private FijarResponsableBuzonCommandHandler Handler(CaeManagerDbContext runtime)
    {
        var actor = ActorAdministrador();
        return new FijarResponsableBuzonCommandHandler(
            new ConexionIntegracionRepository(runtime),
            new AlcanceDatosService(runtime, actor, _tenantDeLaPeticion, new SesionPrivilegiadaAusente()),
            Elegibilidad(runtime, _tenantDeLaPeticion), actor, runtime,
            new TransaccionDeComando(runtime), new BloqueoCarteraUsuario(runtime));
    }

    private static ResponsableBuzonElegible Elegibilidad(CaeManagerDbContext runtime, ITenantActual tenant)
    {
        var directorio = new DirectorioUsuariosTenant(null!, runtime, tenant, new PuertaAccesoDatos(), runtime, null!);
        return new ResponsableBuzonElegible(directorio, directorio, runtime, runtime, tenant);
    }

    private Task<Guid?> LeerResponsableAsync(Guid conexionId) =>
        _propietario.ConexionesIntegracion.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Id == conexionId).Select(c => c.ResponsableUsuarioId).SingleAsync();

    private CaeManagerDbContext CrearRuntime(ITenantActual tenant, ICurrentUserService usuario)
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenant),
                new TenantRlsConnectionInterceptor(tenant, new SinClienteActivo(), usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls),
                new ConcurrenciaOptimistaInterceptor())
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenant);
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    /// <summary>Como el <c>TenantActual</c> real: primero el ámbito explícito, luego el Tenant de la sesión.</summary>
    private sealed class TenantActualDeLaPeticion(Guid tenantDeLaSesion) : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual ?? tenantDeLaSesion;
    }

    private sealed class SinClienteActivo : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private sealed class SesionPrivilegiadaAusente : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }
}
