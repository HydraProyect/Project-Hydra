using CaeManager.Application.Dashboard.Queries;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// ObtenerEstadisticasAprobacionDocumentoQuery (gráfico de Dashboard
/// "gestiones automáticas vs manuales") depende de IApplicationDbContext
/// para unir AprobacionDocumento con Documento/Trabajador y aplicar
/// alcance de cartera — mismo criterio que RevisionIaDocumentoTests.
/// </summary>
public class EstadisticasAprobacionDocumentoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private CaeManagerDbContext _dbContext = null!;
    private Trabajador _trabajadorVisible = null!;
    private Trabajador _trabajadorAjeno = null!;

    public async Task InitializeAsync()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = TenantSeedData.IdPorDefecto };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        _dbContext = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
        await _dbContext.Database.MigrateAsync();

        var empresa = new Empresa("Ibertec S.A.");
        _dbContext.Empresas.Add(empresa);

        _trabajadorVisible = Trabajador.DeEmpresa(empresa.Id, "Alvaro", "Sanchez Martin", "77189989B");
        _trabajadorAjeno = Trabajador.DeEmpresa(empresa.Id, "Otro", "Trabajador Ajeno", "12345678Z");
        _dbContext.Trabajadores.AddRange(_trabajadorVisible, _trabajadorAjeno);

        var tipoApto = await _dbContext.TiposDocumento.FirstAsync(t => t.AmbitoAplicacion == AmbitoAplicacion.Trabajador);
        var fechaEmision = DateOnly.FromDateTime(DateTime.UtcNow);

        var documentoVisibleAutomatico = Documento.DeTrabajador(_trabajadorVisible.Id, tipoApto.Id, fechaEmision, null, "a.pdf");
        var documentoVisibleManual = Documento.DeTrabajador(_trabajadorVisible.Id, tipoApto.Id, fechaEmision, null, "b.pdf");
        var documentoAjeno = Documento.DeTrabajador(_trabajadorAjeno.Id, tipoApto.Id, fechaEmision, null, "c.pdf");
        _dbContext.Documentos.AddRange(documentoVisibleAutomatico, documentoVisibleManual, documentoAjeno);
        await _dbContext.SaveChangesAsync();

        _dbContext.AprobacionesDocumento.AddRange(
            AprobacionDocumento.CrearAutomatica(documentoVisibleAutomatico.Id, 95),
            AprobacionDocumento.CrearManual(documentoVisibleManual.Id, 62, Guid.NewGuid()),
            AprobacionDocumento.CrearAutomatica(documentoAjeno.Id, 97));
        await _dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _dbContext.Database.EnsureDeletedAsync();
        await _dbContext.DisposeAsync();
    }

    [Fact]
    public async Task Cuenta_automaticas_y_manuales_solo_de_trabajadores_visibles()
    {
        var alcance = new AlcanceDatosServiceFalso(trabajadorIds: [_trabajadorVisible.Id]);
        var handler = new ObtenerEstadisticasAprobacionDocumentoQueryHandler(_dbContext, alcance);

        var resultado = await handler.Handle(new ObtenerEstadisticasAprobacionDocumentoQuery(), CancellationToken.None);

        resultado.Automaticas.Should().Be(1);
        resultado.Manuales.Should().Be(1);
    }

    [Fact]
    public async Task Sin_restriccion_de_alcance_cuenta_todas()
    {
        var alcance = new AlcanceDatosServiceFalso();
        var handler = new ObtenerEstadisticasAprobacionDocumentoQueryHandler(_dbContext, alcance);

        var resultado = await handler.Handle(new ObtenerEstadisticasAprobacionDocumentoQuery(), CancellationToken.None);

        resultado.Automaticas.Should().Be(2);
        resultado.Manuales.Should().Be(1);
    }
}
