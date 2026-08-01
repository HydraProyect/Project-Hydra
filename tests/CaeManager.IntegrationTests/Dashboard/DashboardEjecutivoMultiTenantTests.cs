using CaeManager.Application.Common;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.DependencyInjection;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Dashboard;

/// <summary>
/// Verifica el mecanismo de fan-out+merge de <c>ObtenerDashboardEjecutivoQuery</c>
/// contra PostgreSQL real: un usuario del tenant "Consultora" con una
/// <c>AsignacionOperadorDelegado</c> vigente sobre un tenant "Cliente" debe
/// ver la suma de ambos, sin que ninguna query individual cruce el filtro
/// global de tenant (ADR-004 § 7.2 — mismo criterio ya auditado en
/// AislamientoMultiTenantTests, aplicado aquí al camino completo de
/// MediatR en vez de al DbContext en aislamiento).
/// </summary>
public class DashboardEjecutivoMultiTenantTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _usuario = Guid.NewGuid();
    private CaeManagerDbContext _dbContext = null!;
    private ServiceProvider _servicios = null!;
    private Guid _tenantConsultora;
    private Guid _tenantCliente;

    public async Task InitializeAsync()
    {
        var tenantActual = new TenantActualPorAmbito();
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        _dbContext = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
        await _dbContext.Database.MigrateAsync();

        var servicios = new ServiceCollection();
        servicios.AddApplication();
        // AddApplication() registra LoggingBehavior en el pipeline de MediatR
        // (P1-10 de docs/business/MATURITY_REVIEW.md), que pide ILoggerFactory
        // por constructor — este ServiceCollection de test no monta Serilog,
        // así que basta con el sumidero nulo para poder resolver el pipeline.
        servicios.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        servicios.AddSingleton<IApplicationDbContext>(_dbContext);
        servicios.AddSingleton<IUnitOfWork>(_dbContext);
        servicios.AddSingleton<IAlcanceDatosService>(new AlcanceDatosServiceFalso());
        servicios.AddSingleton<ICurrentUserService>(new CurrentUserServiceFalso(_usuario, () => _tenantConsultora));
        _servicios = servicios.BuildServiceProvider();

        var tenantConsultora = new Tenant("Consultora Demo");
        var tenantCliente = new Tenant("Ibertec S.A.");
        _dbContext.Tenants.AddRange(tenantConsultora, tenantCliente);

        var delegacion = new DelegacionTenant(tenantConsultora.Id, tenantCliente.Id);
        _dbContext.DelegacionesTenant.Add(delegacion);
        _dbContext.AsignacionesOperadorDelegado.Add(new AsignacionOperadorDelegado(delegacion.Id, _usuario, "DireccionCae"));
        await _dbContext.SaveChangesAsync();

        _tenantConsultora = tenantConsultora.Id;
        _tenantCliente = tenantCliente.Id;

        await SembrarAsync(_tenantConsultora, "Consultora", trabajadores: 2, centros: 1);
        await SembrarAsync(_tenantCliente, "Ibertec", trabajadores: 5, centros: 3);
    }

    public async Task DisposeAsync()
    {
        _servicios.Dispose();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
        await _dbContext.DisposeAsync();
    }

    [Fact]
    public async Task Fusiona_los_trabajadores_y_centros_de_ambos_tenants()
    {
        var mediator = _servicios.GetRequiredService<IMediator>();

        var resultado = await mediator.Send(new ObtenerDashboardEjecutivoQuery());

        resultado.TotalTenants.Should().Be(2);
        resultado.TrabajadoresActivos.Should().Be(7, "2 de la Consultora + 5 de Ibertec");
        resultado.Centros.Should().Be(4, "1 de la Consultora + 3 de Ibertec");
    }

    [Fact]
    public async Task Ningun_tenant_individual_ve_los_datos_del_otro()
    {
        // Confirma que el fan-out no logra su resultado cruzando el filtro
        // global (que seguiría siendo un fallo de seguridad aunque el total
        // fusionado diera bien por casualidad) — cada tenant, consultado por
        // separado con el mismo AmbitoTenantExplicito que usa el fan-out,
        // solo ve lo suyo.
        using (AmbitoTenantExplicito.Establecer(_tenantConsultora))
        {
            (await _dbContext.Trabajadores.CountAsync()).Should().Be(2);
        }

        using (AmbitoTenantExplicito.Establecer(_tenantCliente))
        {
            (await _dbContext.Trabajadores.CountAsync()).Should().Be(5);
        }
    }

    private async Task SembrarAsync(Guid tenantId, string prefijo, int trabajadores, int centros)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);

        var cliente = new Cliente($"{prefijo} Cliente", "B12345674", esCritico: false);
        var empresa = new Empresa($"{prefijo} Empresa");
        _dbContext.Clientes.Add(cliente);
        _dbContext.Empresas.Add(empresa);
        _dbContext.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));
        await _dbContext.SaveChangesAsync();

        for (var i = 0; i < centros; i++)
            _dbContext.Centros.Add(new Centro(cliente.Id, empresa.Id, $"{prefijo} Centro {i}"));

        for (var i = 0; i < trabajadores; i++)
            _dbContext.Trabajadores.Add(Trabajador.DeEmpresa(empresa.Id, $"{prefijo}{i}", "Apellido", GenerarDni(i)));

        await _dbContext.SaveChangesAsync();
    }

    private static string GenerarDni(int numero)
    {
        const string letrasControl = "TRWAGMYFPDXBNJZSQVHLCKE";
        return $"{numero:D8}{letrasControl[numero % 23]}";
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    private sealed class CurrentUserServiceFalso(Guid usuarioId, Func<Guid?> tenantOrigenId) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(usuarioId);

        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>("DireccionCae");

        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult(tenantOrigenId());

        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }
}
