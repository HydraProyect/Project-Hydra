using CaeManager.Application.Common;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.DependencyInjection;
using CaeManager.Domain.Configuracion;
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
/// Sesión 01 H1 / ROADMAP-UX.md Horizonte 1 nº 1: un operador con rol
/// restringido y cartera vacía (<see cref="IAlcanceDatosService"/> devuelve
/// lista de Clientes vacía, nunca null) debe recibir <c>SinCarteraAsignada</c>
/// en vez de un SLA 100% verde sobre cero documentos.
/// </summary>
public class ObtenerKpisDashboardQuerySinCarteraTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private CaeManagerDbContext _dbContext = null!;
    private TenantActualAmbiental _tenantActual = null!;

    public async Task InitializeAsync()
    {
        _tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(_tenantActual))
            .Options;

        _dbContext = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), _tenantActual);
        await _dbContext.Database.MigrateAsync();

        _dbContext.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));
        await _dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
        await _dbContext.DisposeAsync();
    }

    private ServiceProvider ConstruirServicios(IAlcanceDatosService alcanceDatos)
    {
        var servicios = new ServiceCollection();
        servicios.AddApplication();
        servicios.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        servicios.AddSingleton<CaeManager.Application.Centros.ICentrosQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Configuracion.IConfiguracionQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Documentos.IDocumentosQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Trabajadores.ITrabajadoresQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Visitas.IVisitasQueryContext>(_dbContext);
        servicios.AddSingleton(alcanceDatos);
        servicios.AddSingleton<ICurrentUserService>(new CurrentUserServiceFalso(tenantOrigenId: _tenant));
        servicios.AddSingleton<ITenantActual>(_tenantActual);
        return servicios.BuildServiceProvider();
    }

    [Fact]
    public async Task Cartera_vacia_marca_SinCarteraAsignada_y_no_infla_el_SLA_al_100_por_cien()
    {
        using var servicios = ConstruirServicios(new AlcanceDatosServiceFalso(clienteIds: [], centroIds: [], trabajadorIds: []));
        var mediator = servicios.GetRequiredService<IMediator>();

        var resultado = await mediator.Send(new ObtenerKpisDashboardQuery());

        resultado.SinCarteraAsignada.Should().BeTrue();
        resultado.TrabajadoresActivos.Should().Be(0);
    }

    [Fact]
    public async Task Acceso_total_sin_datos_no_se_confunde_con_cartera_vacia()
    {
        using var servicios = ConstruirServicios(new AlcanceDatosServiceFalso());
        var mediator = servicios.GetRequiredService<IMediator>();

        var resultado = await mediator.Send(new ObtenerKpisDashboardQuery());

        resultado.SinCarteraAsignada.Should().BeFalse("Administrador/DireccionCae/Consulta ven todo (alcance null), aunque el tenant esté vacío");
    }
}
