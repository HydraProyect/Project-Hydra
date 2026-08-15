using CaeManager.Application.Documentos.Commands.ResolverRevisionIaDocumento;
using CaeManager.Application.Documentos.Queries.ObtenerRevisionesIaPendientes;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.DocumentosIa;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// ObtenerRevisionesIaPendientesQuery/ResolverRevisionIaDocumentoCommand
/// (Issue #19) dependen de IApplicationDbContext para resolver el
/// Trabajador dueño del Documento y su alcance de cartera — mismo criterio
/// que AlcancePorIdTests: SQLite real, no fakes de contexto.
/// </summary>
public class RevisionIaDocumentoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _usuarioIdDePrueba = Guid.NewGuid();
    private CaeManagerDbContext _dbContext = null!;
    private Trabajador _trabajadorVisible = null!;
    private Trabajador _trabajadorAjeno = null!;
    private RevisionIaDocumento _revisionVisible = null!;
    private RevisionIaDocumento _revisionAjena = null!;

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

        var documentoVisible = Documento.DeTrabajador(_trabajadorVisible.Id, tipoApto.Id, fechaEmision, null, "archivo.pdf");
        var documentoAjeno = Documento.DeTrabajador(_trabajadorAjeno.Id, tipoApto.Id, fechaEmision, null, "archivo.pdf");
        _dbContext.Documentos.AddRange(documentoVisible, documentoAjeno);
        await _dbContext.SaveChangesAsync();

        _revisionVisible = RevisionIaDocumento.Crear(documentoVisible.Id, 62, "Apto médico", fechaEmision, null, true, "Confianza baja (62%)");
        _revisionAjena = RevisionIaDocumento.Crear(documentoAjeno.Id, 55, "Apto médico", fechaEmision, null, true, "Confianza baja (55%)");
        _dbContext.RevisionesIaDocumento.AddRange(_revisionVisible, _revisionAjena);
        await _dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _dbContext.Database.EnsureDeletedAsync();
        await _dbContext.DisposeAsync();
    }

    [Fact]
    public async Task La_query_solo_devuelve_revisiones_de_trabajadores_visibles()
    {
        var alcance = new AlcanceDatosServiceFalso(trabajadorIds: [_trabajadorVisible.Id]);
        var handler = new ObtenerRevisionesIaPendientesQueryHandler(_dbContext, _dbContext, _dbContext, _dbContext, alcance);

        var resultado = await handler.Handle(new ObtenerRevisionesIaPendientesQuery(), CancellationToken.None);

        resultado.Should().ContainSingle();
        resultado[0].Id.Should().Be(_revisionVisible.Id);
        resultado[0].PropietarioNombre.Should().Contain(_trabajadorVisible.Nombre);
    }

    [Fact]
    public async Task Resolver_marca_la_revision_como_hecha_cuando_el_trabajador_es_visible()
    {
        var alcance = new AlcanceDatosServiceFalso(trabajadorIds: [_trabajadorVisible.Id]);
        var handler = new ResolverRevisionIaDocumentoCommandHandler(
            new RevisionIaDocumentoRepository(_dbContext), new AprobacionDocumentoRepository(_dbContext),
            new AuditoriaExtraccionIaRepository(_dbContext), _dbContext,
            alcance, new CurrentUserServiceFalso(usuarioId: _usuarioIdDePrueba), _dbContext);

        var resultado = await handler.Handle(new ResolverRevisionIaDocumentoCommand(_revisionVisible.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        (await _dbContext.RevisionesIaDocumento.FirstAsync(r => r.Id == _revisionVisible.Id)).Resuelta.Should().BeTrue();

        var aprobacion = await _dbContext.AprobacionesDocumento.FirstAsync(a => a.DocumentoId == _revisionVisible.DocumentoId);
        aprobacion.Tipo.Should().Be(TipoAprobacionDocumento.Manual);
        aprobacion.UsuarioId.Should().Be(_usuarioIdDePrueba);
        aprobacion.ConfianzaGeneral.Should().Be(_revisionVisible.ConfianzaGeneral);
    }

    [Fact]
    public async Task Resolver_marca_como_descartada_manual_la_auditoria_de_ia_ligada_al_documento()
    {
        var auditoria = AuditoriaExtraccionIa.Crear(
            new string('a', AuditoriaExtraccionIa.LongitudHash), "Apto médico", "anthropic", 700,
            null, 0.01m, 1, _revisionVisible.ConfianzaGeneral, null, _revisionVisible.DocumentoId);
        _dbContext.AuditoriasExtraccionIa.Add(auditoria);
        await _dbContext.SaveChangesAsync();

        var alcance = new AlcanceDatosServiceFalso(trabajadorIds: [_trabajadorVisible.Id]);
        var handler = new ResolverRevisionIaDocumentoCommandHandler(
            new RevisionIaDocumentoRepository(_dbContext), new AprobacionDocumentoRepository(_dbContext),
            new AuditoriaExtraccionIaRepository(_dbContext), _dbContext,
            alcance, new CurrentUserServiceFalso(usuarioId: _usuarioIdDePrueba), _dbContext);

        var resultado = await handler.Handle(new ResolverRevisionIaDocumentoCommand(_revisionVisible.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var auditoriaActualizada = await _dbContext.AuditoriasExtraccionIa.SingleAsync(a => a.Id == auditoria.Id);
        auditoriaActualizada.DecisionHumana.Should().Be(DecisionHumanaIa.DescartadaManual);
        auditoriaActualizada.UsuarioDecisionId.Should().Be(_usuarioIdDePrueba);
    }

    [Fact]
    public async Task Resolver_falla_como_no_encontrada_cuando_el_trabajador_no_es_visible()
    {
        var alcance = new AlcanceDatosServiceFalso(trabajadorIds: [_trabajadorVisible.Id]);
        var handler = new ResolverRevisionIaDocumentoCommandHandler(
            new RevisionIaDocumentoRepository(_dbContext), new AprobacionDocumentoRepository(_dbContext),
            new AuditoriaExtraccionIaRepository(_dbContext), _dbContext,
            alcance, new CurrentUserServiceFalso(usuarioId: _usuarioIdDePrueba), _dbContext);

        var resultado = await handler.Handle(new ResolverRevisionIaDocumentoCommand(_revisionAjena.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("RevisionIa.NoEncontrada");
        (await _dbContext.RevisionesIaDocumento.FirstAsync(r => r.Id == _revisionAjena.Id)).Resuelta.Should().BeFalse();
    }

    [Fact]
    public async Task Resolver_falla_si_ya_estaba_resuelta()
    {
        _revisionVisible.Resolver();
        await _dbContext.SaveChangesAsync();

        var alcance = new AlcanceDatosServiceFalso(trabajadorIds: [_trabajadorVisible.Id]);
        var handler = new ResolverRevisionIaDocumentoCommandHandler(
            new RevisionIaDocumentoRepository(_dbContext), new AprobacionDocumentoRepository(_dbContext),
            new AuditoriaExtraccionIaRepository(_dbContext), _dbContext,
            alcance, new CurrentUserServiceFalso(usuarioId: _usuarioIdDePrueba), _dbContext);

        var resultado = await handler.Handle(new ResolverRevisionIaDocumentoCommand(_revisionVisible.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("RevisionIa.YaResuelta");
    }
}
