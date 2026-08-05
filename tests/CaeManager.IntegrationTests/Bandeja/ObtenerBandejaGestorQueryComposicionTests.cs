using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RequisitosDocumentales;
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

namespace CaeManager.IntegrationTests.Bandeja;

/// <summary>
/// La composición de <c>ObtenerBandejaGestorQuery</c> vía <see cref="IMediator"/>
/// (fan-out a ObtenerAlertasQuery + ObtenerRevisionesIaPendientesQuery +
/// ObtenerRequisitosDocumentalesPendientesQuery) ya está probada en su lógica
/// de fusión pura por <c>ObtenerBandejaGestorQueryHandlerTests</c>
/// (Application.Tests, sin PostgreSQL). Este test cubre lo que aquel no
/// puede: que el cableado real de DI (mismo <c>AddApplication()</c> que usa
/// la app) resuelve y compone las tres Queries correctamente contra datos
/// reales — mismo motivo que <c>DashboardEjecutivoMultiTenantTests</c>
/// prueba su propio fan-out con un contenedor real en vez de solo mocks.
/// </summary>
public class ObtenerBandejaGestorQueryComposicionTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private CaeManagerDbContext _dbContext = null!;
    private ServiceProvider _servicios = null!;

    public async Task InitializeAsync()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        _dbContext = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
        await _dbContext.Database.MigrateAsync();

        var servicios = new ServiceCollection();
        servicios.AddApplication();
        servicios.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        servicios.AddSingleton<ITenantActual>(tenantActual);
        servicios.AddSingleton<IUnitOfWork>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Clientes.IClientesQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Empresas.IEmpresasQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Centros.ICentrosQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Trabajadores.ITrabajadoresQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.TiposDocumento.ITiposDocumentoQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Documentos.IDocumentosQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Asignaciones.IAsignacionesQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Configuracion.IConfiguracionQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.RequisitosDocumentales.IRequisitosDocumentalesQueryContext>(_dbContext);
        servicios.AddSingleton<IAlcanceDatosService>(new AlcanceDatosServiceFalso());
        servicios.AddSingleton<ICurrentUserService>(new CurrentUserServiceFalso(Guid.NewGuid(), tenantOrigenId: _tenant));
        _servicios = servicios.BuildServiceProvider();

        var cliente = new Cliente("Bandeja Composición S.L.", "B12345674", esCritico: false);
        var empresa = new Empresa("Empresa Bandeja S.L.", "B87654323");
        _dbContext.Clientes.Add(cliente);
        _dbContext.Empresas.Add(empresa);
        _dbContext.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));
        await _dbContext.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro de la bandeja");
        _dbContext.Centros.Add(centro);

        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Bandeja", "Trabajador", "77189989B");
        _dbContext.Trabajadores.Add(trabajador);

        var tipoObligatorio = new TipoDocumento("Apto médico", 12, true, 1, AmbitoAplicacion.Trabajador, esObligatorio: true);
        _dbContext.TiposDocumento.Add(tipoObligatorio);
        await _dbContext.SaveChangesAsync();

        // Faltante: asignación activa a un tipo obligatorio sin ningún Documento.
        _dbContext.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, DateOnly.FromDateTime(DateTime.UtcNow)));

        // Requisito bloqueante sin cumplir.
        _dbContext.RequisitosDocumentales.Add(new RequisitoDocumental(centro.Id, "PSS firmado", null, bloqueaAcceso: true));

        await _dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        _servicios.Dispose();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
        await _dbContext.DisposeAsync();
    }

    [Fact]
    public async Task Compone_faltantes_y_requisitos_pendientes_en_una_sola_cola_priorizada()
    {
        var mediator = _servicios.GetRequiredService<IMediator>();

        var resultado = await mediator.Send(new ObtenerBandejaGestorQuery());

        resultado.Should().HaveCount(2);
        resultado[0].Tipo.Should().Be(TipoItemBandeja.Faltante, "Faltante tiene la prioridad más alta");
        resultado[1].Tipo.Should().Be(TipoItemBandeja.RequisitoPendiente);
    }
}
