using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Application.Integraciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Integraciones;

/// <summary>
/// Verifica el catálogo <c>ProveedorPlataformaCae</c> (Parte 2 § (a),
/// PLAN-EJECUCION-UX.md) contra una base de datos real, con la migración de
/// semilla aplicada — lo que <see cref="ResolucionProveedorPlataformaCaeServiceTests"/>
/// (Application.Tests) no puede cubrir: que la semilla realmente llegó a la
/// tabla sin colisión de Id (ver el bug real de <c>DerivarIdDominio</c>
/// detectado al generar la migración) y que la resolución de EF Core
/// (Contains/Select) traduce igual que la lógica pura ya probada.
/// </summary>
public class ResolucionProveedorPlataformaCaeServiceTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private CaeManagerDbContext _dbContext = null!;
    private ServiceProvider _servicios = null!;

    public async Task InitializeAsync()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = Guid.NewGuid() };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;

        _dbContext = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
        await _dbContext.Database.MigrateAsync();

        var servicios = new ServiceCollection();
        servicios.AddApplication();
        servicios.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        servicios.AddSingleton<IUnitOfWork>(_dbContext);
        servicios.AddSingleton<IProveedoresPlataformaCaeQueryContext>(_dbContext);
        _servicios = servicios.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        _servicios.Dispose();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
        await _dbContext.DisposeAsync();
    }

    [Fact]
    public async Task La_migracion_siembra_los_23_proveedores_y_27_dominios_del_catalogo()
    {
        (await _dbContext.ProveedoresPlataformaCae.CountAsync()).Should().Be(23);
        (await _dbContext.DominiosProveedorPlataformaCae.CountAsync()).Should().Be(27);
    }

    [Fact]
    public async Task Arch_y_Opground_se_siembran_inactivos()
    {
        var inactivos = await _dbContext.ProveedoresPlataformaCae
            .Where(p => !p.Activo)
            .Select(p => p.Codigo)
            .ToListAsync();

        inactivos.Should().BeEquivalentTo("arch", "opground");
    }

    [Fact]
    public async Task Resuelve_un_unico_proveedor_para_un_dominio_sin_ambiguedad()
    {
        var servicio = _servicios.GetRequiredService<IResolucionProveedorPlataformaCaeService>();

        var resultado = await servicio.ResolverPorUrlAsync("https://app.twind.io/login");

        resultado.Should().ContainSingle().Which.Nombre.Should().Be("Twind");
    }

    [Fact]
    public async Task Resuelve_un_subdominio_de_cliente_contra_el_proveedor_de_sufijo_por_dominio()
    {
        var servicio = _servicios.GetRequiredService<IResolucionProveedorPlataformaCaeService>();

        var resultado = await servicio.ResolverPorUrlAsync("https://cliente123.ergasia.es/acceso");

        resultado.Should().ContainSingle().Which.Nombre.Should().Be("Ergasia");
    }

    [Fact]
    public async Task Resuelve_multi_match_real_de_la_semilla_Sabentis_y_Quiron_Prevencion()
    {
        var servicio = _servicios.GetRequiredService<IResolucionProveedorPlataformaCaeService>();

        var resultado = await servicio.ResolverPorUrlAsync("https://quironprevencion.com");

        resultado.Should().HaveCount(2);
        resultado.Select(p => p.Nombre).Should().BeEquivalentTo("Sabentis", "Quirón Prevención");
    }

    [Fact]
    public async Task Sin_dominio_generico_DocuPRL_no_resuelve_por_dominio()
    {
        var servicio = _servicios.GetRequiredService<IResolucionProveedorPlataformaCaeService>();

        var resultado = await servicio.ResolverPorUrlAsync("https://cliente-x.example.net");

        resultado.Should().BeEmpty();
    }

    [Fact]
    public async Task Resuelve_una_plataforma_conocida_por_el_dominio_del_remitente_de_un_correo()
    {
        var servicio = _servicios.GetRequiredService<IResolucionProveedorPlataformaCaeService>();

        var resultado = await servicio.ResolverPorDominioCorreoAsync("notificaciones@app.twind.io");

        resultado.Should().ContainSingle().Which.Nombre.Should().Be("Twind");
    }

    [Fact]
    public async Task Un_remitente_de_dominio_desconocido_no_resuelve_ninguna_plataforma()
    {
        var servicio = _servicios.GetRequiredService<IResolucionProveedorPlataformaCaeService>();

        var resultado = await servicio.ResolverPorDominioCorreoAsync("gestor@cliente-real.com");

        resultado.Should().BeEmpty();
    }
}
