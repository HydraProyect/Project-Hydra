using CaeManager.Application.Common;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Integraciones;

/// <summary>
/// La consulta del ciclo diario que busca las conexiones con la marca del hueco por registrar
/// (<see cref="ConexionIntegracionRepository.ObtenerIdsConMarcaHuecoPorRegistrarAsync"/>), <b>como
/// <c>cae_app_runtime</c></b> y con los interceptores de sellado y de sesión RLS de producción. El ciclo corre sin usuario,
/// dentro del ámbito explícito de cada Tenant propietario activo, como aquí. Dos Tenants propietarios, cada uno con una
/// conexión Deshabilitada con la marca y otra sin ella; el ámbito es el del primero. Lo que se mide: la consulta devuelve
/// solo la conexión marcada del Tenant en contexto y nada del otro. El control demuestra que, sin filtro global de EF, es
/// RLS quien oculta la conexión marcada del otro Tenant, y que esa conexión existe. La siembra va como propietario de la
/// base, porque no es lo que se mide.
/// </summary>
public class MarcaHuecoPorRegistrarBajoRlsTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    private CaeManagerDbContext _propietario = null!;
    private Guid _tenantEnContexto;
    private Guid _tenantAjeno;
    private Guid _marcadaPropia;
    private Guid _sinMarcaPropia;
    private Guid _marcadaAjena;
    private Guid _sinMarcaAjena;

    public async Task InitializeAsync()
    {
        var tenantPorAmbito = new TenantActualPorAmbito();
        var opcionesPropietario = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
            .Options;
        _propietario = new CaeManagerDbContext(opcionesPropietario, new EphemeralDataProtectionProvider(), tenantPorAmbito);
        await _propietario.Database.MigrateAsync();

        var tenantEnContexto = new Tenant("Tenant propietario en contexto");
        var tenantAjeno = new Tenant("Tenant propietario ajeno");
        _propietario.Tenants.AddRange(tenantEnContexto, tenantAjeno);
        await _propietario.SaveChangesAsync();
        _tenantEnContexto = tenantEnContexto.Id;
        _tenantAjeno = tenantAjeno.Id;

        (_marcadaPropia, _sinMarcaPropia) = await SembrarAsync(_tenantEnContexto, "propio.test");
        (_marcadaAjena, _sinMarcaAjena) = await SembrarAsync(_tenantAjeno, "ajeno.test");
    }

    public async Task DisposeAsync()
    {
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    /// <summary>
    /// Control del instrumento: las cuatro conexiones existen, las dos marcadas lo están de verdad en la base, y la
    /// conexión de runtime está bajo RLS efectiva (sin filtro global de EF, la marcada ajena no se ve y la propia sí).
    /// </summary>
    [Fact]
    public async Task La_siembra_tiene_una_marcada_por_Tenant_y_la_conexion_va_bajo_RLS_efectiva()
    {
        (await _propietario.ConexionesIntegracion.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.MarcaHuecoPorRegistrarDesdeUtc != null)
                .Select(c => c.Id).ToListAsync())
            .Should().BeEquivalentTo([_marcadaPropia, _marcadaAjena], "control: la base guarda la marca en las dos marcadas");
        (await _propietario.ConexionesIntegracion.IgnoreQueryFilters()
                .CountAsync(c => c.Id == _sinMarcaPropia || c.Id == _sinMarcaAjena))
            .Should().Be(2, "control: las dos sin marca existen");

        using var ambito = AmbitoTenantExplicito.Establecer(_tenantEnContexto);
        await using var runtime = CrearRuntime(new TenantActualPorAmbito());

        (await runtime.ConexionesIntegracion.IgnoreQueryFilters().CountAsync(c => c.Id == _marcadaAjena)).Should().Be(0,
            "sin filtro global de EF, solo RLS puede ocultar la conexión marcada del Tenant ajeno");
        (await runtime.ConexionesIntegracion.IgnoreQueryFilters().CountAsync(c => c.Id == _marcadaPropia)).Should().Be(1,
            "control positivo: la conexión marcada del Tenant en contexto sí se ve");
    }

    [Fact]
    public async Task Devuelve_solo_las_marcadas_del_Tenant_propietario_en_contexto()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantEnContexto);
        await using var runtime = CrearRuntime(new TenantActualPorAmbito());

        var ids = await new ConexionIntegracionRepository(runtime).ObtenerIdsConMarcaHuecoPorRegistrarAsync();

        ids.Should().Equal([_marcadaPropia], "ni la propia sin marca ni nada del Tenant ajeno");
    }

    /// <summary>
    /// La misma consulta en el ámbito del otro Tenant devuelve la suya: el resultado depende del Tenant en contexto, no
    /// de qué conexión se sembró primero.
    /// </summary>
    [Fact]
    public async Task En_el_ambito_del_otro_Tenant_devuelve_solo_la_suya()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantAjeno);
        await using var runtime = CrearRuntime(new TenantActualPorAmbito());

        var ids = await new ConexionIntegracionRepository(runtime).ObtenerIdsConMarcaHuecoPorRegistrarAsync();

        ids.Should().Equal([_marcadaAjena]);
    }

    /// <summary>
    /// Fallo cerrado: sin ámbito de Tenant, el contexto RLS de la conexión no lleva Tenant y la política no iguala ninguna
    /// fila. La
    /// consulta no devuelve las marcadas de los dos Tenants, y sin filtro global de EF RLS tampoco deja ver ninguna.
    /// </summary>
    [Fact]
    public async Task Sin_ambito_de_Tenant_no_devuelve_ninguna()
    {
        AmbitoTenantExplicito.TenantIdActual.Should().BeNull("control: el test corre fuera de cualquier ámbito");
        await using var runtime = CrearRuntime(new TenantActualPorAmbito());

        var ids = await new ConexionIntegracionRepository(runtime).ObtenerIdsConMarcaHuecoPorRegistrarAsync();

        ids.Should().BeEmpty();
        (await runtime.ConexionesIntegracion.IgnoreQueryFilters()
                .CountAsync(c => c.Id == _marcadaPropia || c.Id == _marcadaAjena))
            .Should().Be(0, "sin filtro global de EF, es RLS quien no deja ver ninguna");
    }

    /// <summary>
    /// Una Deshabilitada con la marca y otra Deshabilitada sin ella (la desconexión sin suscripción que borrar no deja
    /// nada por registrar), selladas en <paramref name="tenantId"/>.
    /// </summary>
    private async Task<(Guid Marcada, Guid SinMarca)> SembrarAsync(Guid tenantId, string dominio)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);

        var marcada = new ConexionIntegracion($"cae@{dominio}", "Buzón con hueco por registrar");
        marcada.Deshabilitar(DateTime.UtcNow.AddMinutes(-5));
        var sinMarca = new ConexionIntegracion($"avisos@{dominio}", "Buzón desconectado sin hueco");
        sinMarca.Deshabilitar();
        marcada.MarcaHuecoPorRegistrarDesdeUtc.Should().NotBeNull("control: Deshabilitar fija la marca en un buzón M365");
        sinMarca.MarcaHuecoPorRegistrarDesdeUtc.Should().BeNull();

        _propietario.ConexionesIntegracion.AddRange(marcada, sinMarca);
        await _propietario.SaveChangesAsync();
        return (marcada.Id, sinMarca.Id);
    }

    private CaeManagerDbContext CrearRuntime(ITenantActual tenant)
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenant),
                new TenantRlsConnectionInterceptor(tenant, new SinClienteActivo(), new CurrentUserServiceFalso(),
                    BaseDatosPostgresDePruebas.FirmanteContextoRls),
                new ConcurrenciaOptimistaInterceptor())
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenant);
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    private sealed class SinClienteActivo : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
