using CaeManager.Application.Documentos.Commands.AplicarDeteccionIaDocumento;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.DocumentosIa;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// Fase D ("Aceptar lo detectado por la IA"): a diferencia de
/// ResolverRevisionIaDocumentoCommand (nunca toca el Documento, ver
/// RevisionIaDocumentoTests), este Command sí lo renueva con la fecha de
/// emisión que detectó la IA.
/// </summary>
public class AplicarDeteccionIaDocumentoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _usuarioId = Guid.NewGuid();
    private CaeManagerDbContext _dbContext = null!;
    private Trabajador _trabajador = null!;
    private TipoDocumento _tipoConVencimientoAutomatico = null!;
    private TipoDocumento _tipoConVencimientoManual = null!;

    public async Task InitializeAsync()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        _dbContext = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
        await _dbContext.Database.MigrateAsync();

        var empresa = new Empresa("Detección IA S.L.");
        _dbContext.Empresas.Add(empresa);

        _trabajador = Trabajador.DeEmpresa(empresa.Id, "Ana", "García", "77189989B");
        _dbContext.Trabajadores.Add(_trabajador);

        _tipoConVencimientoAutomatico = new TipoDocumento("Apto médico", 12, true, 1, AmbitoAplicacion.Trabajador, esObligatorio: true);
        _tipoConVencimientoManual = new TipoDocumento("Formación PRL", null, false, 2, AmbitoAplicacion.Trabajador, esObligatorio: true);
        _dbContext.TiposDocumento.AddRange(_tipoConVencimientoAutomatico, _tipoConVencimientoManual);
        await _dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _dbContext.Database.EnsureDeletedAsync();
        await _dbContext.DisposeAsync();
    }

    private AplicarDeteccionIaDocumentoCommandHandler CrearHandler(IReadOnlyList<Guid>? trabajadorIds = null) =>
        new(new RevisionIaDocumentoRepository(_dbContext), new DocumentoRepository(_dbContext), new AprobacionDocumentoRepository(_dbContext),
            new AuditoriaExtraccionIaRepository(_dbContext),
            _dbContext, new AlcanceDatosServiceFalso(trabajadorIds: trabajadorIds ?? [_trabajador.Id]), _dbContext,
            new CurrentUserServiceFalso(usuarioId: _usuarioId), new PublisherFalso(), _dbContext);

    [Fact]
    public async Task Renueva_el_documento_con_la_fecha_detectada_y_recalcula_el_vencimiento_automatico()
    {
        var fechaOriginal = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-1);
        var documento = Documento.DeTrabajador(_trabajador.Id, _tipoConVencimientoAutomatico.Id, fechaOriginal, null);
        _dbContext.Documentos.Add(documento);
        await _dbContext.SaveChangesAsync();

        var fechaDetectada = DateOnly.FromDateTime(DateTime.UtcNow);
        var revision = RevisionIaDocumento.Crear(
            documento.Id, 92, "Apto médico", fechaDetectada, fechaDetectada.AddMonths(6), true, "Confianza baja");
        _dbContext.RevisionesIaDocumento.Add(revision);
        await _dbContext.SaveChangesAsync();

        var resultado = await CrearHandler().Handle(new AplicarDeteccionIaDocumentoCommand(revision.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var documentoActualizado = await _dbContext.Documentos.SingleAsync(d => d.Id == documento.Id);
        documentoActualizado.FechaEmision.Should().Be(fechaDetectada);
        // El tipo calcula vigencia automática (12 meses) — el vencimiento
        // real ignora el que detectó la IA (6 meses), igual que RenovarDocumentoCommand.
        documentoActualizado.FechaVencimiento.Should().Be(fechaDetectada.AddMonths(12));

        (await _dbContext.RevisionesIaDocumento.SingleAsync(r => r.Id == revision.Id)).Resuelta.Should().BeTrue();
        var aprobacion = await _dbContext.AprobacionesDocumento.SingleAsync(a => a.DocumentoId == documento.Id);
        aprobacion.UsuarioId.Should().Be(_usuarioId);
    }

    [Fact]
    public async Task Marca_como_confirmada_manual_la_auditoria_de_ia_ligada_al_documento()
    {
        var fechaOriginal = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-1);
        var documento = Documento.DeTrabajador(_trabajador.Id, _tipoConVencimientoAutomatico.Id, fechaOriginal, null);
        _dbContext.Documentos.Add(documento);

        var fechaDetectada = DateOnly.FromDateTime(DateTime.UtcNow);
        var auditoria = AuditoriaExtraccionIa.Crear(
            new string('a', AuditoriaExtraccionIa.LongitudHash), "Apto médico", "anthropic", 900,
            null, 0.01m, 1, 92, null, documento.Id);
        _dbContext.AuditoriasExtraccionIa.Add(auditoria);

        var revision = RevisionIaDocumento.Crear(
            documento.Id, 92, "Apto médico", fechaDetectada, fechaDetectada.AddMonths(6), true, "Confianza baja");
        _dbContext.RevisionesIaDocumento.Add(revision);
        await _dbContext.SaveChangesAsync();

        var resultado = await CrearHandler().Handle(new AplicarDeteccionIaDocumentoCommand(revision.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var auditoriaActualizada = await _dbContext.AuditoriasExtraccionIa.SingleAsync(a => a.Id == auditoria.Id);
        auditoriaActualizada.DecisionHumana.Should().Be(DecisionHumanaIa.ConfirmadaManual);
        auditoriaActualizada.UsuarioDecisionId.Should().Be(_usuarioId);
    }

    [Fact]
    public async Task Usa_la_fecha_de_vencimiento_detectada_cuando_el_tipo_no_calcula_vigencia_automatica()
    {
        var documento = Documento.DeTrabajador(_trabajador.Id, _tipoConVencimientoManual.Id, DateOnly.FromDateTime(DateTime.UtcNow), null);
        _dbContext.Documentos.Add(documento);
        await _dbContext.SaveChangesAsync();

        var fechaDetectada = DateOnly.FromDateTime(DateTime.UtcNow);
        var vencimientoDetectado = fechaDetectada.AddYears(3);
        var revision = RevisionIaDocumento.Crear(
            documento.Id, 90, "Formación PRL", fechaDetectada, vencimientoDetectado, true, "Confianza baja");
        _dbContext.RevisionesIaDocumento.Add(revision);
        await _dbContext.SaveChangesAsync();

        var resultado = await CrearHandler().Handle(new AplicarDeteccionIaDocumentoCommand(revision.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        (await _dbContext.Documentos.SingleAsync(d => d.Id == documento.Id)).FechaVencimiento.Should().Be(vencimientoDetectado);
    }

    [Fact]
    public async Task Falla_si_la_ia_no_detecto_fecha_de_emision()
    {
        var documento = Documento.DeTrabajador(_trabajador.Id, _tipoConVencimientoAutomatico.Id, DateOnly.FromDateTime(DateTime.UtcNow), null);
        _dbContext.Documentos.Add(documento);
        await _dbContext.SaveChangesAsync();

        var revision = RevisionIaDocumento.Crear(documento.Id, 40, null, null, null, null, "Confianza muy baja");
        _dbContext.RevisionesIaDocumento.Add(revision);
        await _dbContext.SaveChangesAsync();

        var resultado = await CrearHandler().Handle(new AplicarDeteccionIaDocumentoCommand(revision.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("RevisionIa.SinFechaDetectada");
    }

    [Fact]
    public async Task Falla_si_la_revision_ya_estaba_resuelta()
    {
        var documento = Documento.DeTrabajador(_trabajador.Id, _tipoConVencimientoAutomatico.Id, DateOnly.FromDateTime(DateTime.UtcNow), null);
        _dbContext.Documentos.Add(documento);
        await _dbContext.SaveChangesAsync();

        var revision = RevisionIaDocumento.Crear(
            documento.Id, 92, "Apto médico", DateOnly.FromDateTime(DateTime.UtcNow), null, true, "Confianza baja");
        revision.Resolver();
        _dbContext.RevisionesIaDocumento.Add(revision);
        await _dbContext.SaveChangesAsync();

        var resultado = await CrearHandler().Handle(new AplicarDeteccionIaDocumentoCommand(revision.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("RevisionIa.YaResuelta");
    }

    [Fact]
    public async Task Falla_como_no_encontrada_cuando_el_trabajador_no_es_visible()
    {
        var documento = Documento.DeTrabajador(_trabajador.Id, _tipoConVencimientoAutomatico.Id, DateOnly.FromDateTime(DateTime.UtcNow), null);
        _dbContext.Documentos.Add(documento);
        await _dbContext.SaveChangesAsync();

        var revision = RevisionIaDocumento.Crear(
            documento.Id, 92, "Apto médico", DateOnly.FromDateTime(DateTime.UtcNow), null, true, "Confianza baja");
        _dbContext.RevisionesIaDocumento.Add(revision);
        await _dbContext.SaveChangesAsync();

        var resultado = await CrearHandler(trabajadorIds: [Guid.NewGuid()])
            .Handle(new AplicarDeteccionIaDocumentoCommand(revision.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("RevisionIa.NoEncontrada");
    }
}
