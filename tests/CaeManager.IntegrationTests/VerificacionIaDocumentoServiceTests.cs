using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Verificacion;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
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
/// VerificacionIaDocumentoService (Issue #19) depende de IApplicationDbContext
/// para leer el Documento/TipoDocumento reales — se prueba contra SQLite real,
/// mismo criterio que DeteccionTrabajadoresServiceTests. La lectura por IA en
/// sí (Anthropic) se sustituye por un fake determinista.
/// </summary>
public class VerificacionIaDocumentoServiceTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private CaeManagerDbContext _dbContext = null!;
    private Empresa _empresa = null!;
    private TipoDocumento _tipoApto = null!;

    public async Task InitializeAsync()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = TenantSeedData.IdPorDefecto };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        _dbContext = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
        await _dbContext.Database.MigrateAsync();

        _empresa = new Empresa("Ibertec S.A.");
        _dbContext.Empresas.Add(_empresa);

        _tipoApto = await _dbContext.TiposDocumento.FirstAsync(t => t.AmbitoAplicacion == AmbitoAplicacion.Trabajador);
        _tipoApto.EstablecerVerificacionIaActiva(true);

        await _dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _dbContext.Database.EnsureDeletedAsync();
        await _dbContext.DisposeAsync();
    }

    private VerificacionIaDocumentoService CrearServicio(IExtraccionMetadatosDocumentoIaService extraccion, IFileStorageService? almacenamiento = null) =>
        new(_dbContext, almacenamiento ?? new AlmacenamientoFalso(), extraccion,
            new RevisionIaDocumentoRepository(_dbContext), new AprobacionDocumentoRepository(_dbContext), _dbContext,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<VerificacionIaDocumentoService>.Instance);

    private Trabajador CrearTrabajador() => Trabajador.DeEmpresa(_empresa.Id, "Alvaro", "Sanchez Martin", "77189989B");

    [Fact]
    public async Task Genera_una_revision_pendiente_cuando_la_confianza_es_baja()
    {
        var trabajador = CrearTrabajador();
        _dbContext.Trabajadores.Add(trabajador);
        var fechaEmision = DateOnly.FromDateTime(DateTime.UtcNow);
        var documento = Documento.DeTrabajador(trabajador.Id, _tipoApto.Id, fechaEmision, null, "archivo.pdf");
        _dbContext.Documentos.Add(documento);
        await _dbContext.SaveChangesAsync();

        var extraido = new MetadatosDocumentoExtraidosDto("Apto médico", fechaEmision, null, true, 62, "Escaneado con baja resolución");
        var servicio = CrearServicio(new ExtraccionIaFalsa(Result.Exito(extraido)));

        await servicio.ProcesarDocumentoAsync(documento.Id);

        var revisiones = await _dbContext.RevisionesIaDocumento.Where(r => r.DocumentoId == documento.Id).ToListAsync();
        revisiones.Should().ContainSingle();
        revisiones[0].ConfianzaGeneral.Should().Be(62);
        revisiones[0].Motivo.Should().Contain("Confianza baja");
        revisiones[0].Resuelta.Should().BeFalse();
    }

    [Fact]
    public async Task Genera_una_revision_cuando_la_fecha_detectada_no_coincide()
    {
        var trabajador = CrearTrabajador();
        _dbContext.Trabajadores.Add(trabajador);
        var fechaEmision = DateOnly.FromDateTime(DateTime.UtcNow);
        var documento = Documento.DeTrabajador(trabajador.Id, _tipoApto.Id, fechaEmision, null, "archivo.pdf");
        _dbContext.Documentos.Add(documento);
        await _dbContext.SaveChangesAsync();

        var extraido = new MetadatosDocumentoExtraidosDto("Apto médico", fechaEmision.AddDays(-30), null, true, 99, null);
        var servicio = CrearServicio(new ExtraccionIaFalsa(Result.Exito(extraido)));

        await servicio.ProcesarDocumentoAsync(documento.Id);

        var revisiones = await _dbContext.RevisionesIaDocumento.Where(r => r.DocumentoId == documento.Id).ToListAsync();
        revisiones.Should().ContainSingle();
        revisiones[0].Motivo.Should().Contain("no coincide");
    }

    [Fact]
    public async Task No_genera_ninguna_revision_cuando_todo_coincide_con_alta_confianza()
    {
        var trabajador = CrearTrabajador();
        _dbContext.Trabajadores.Add(trabajador);
        var fechaEmision = DateOnly.FromDateTime(DateTime.UtcNow);
        var documento = Documento.DeTrabajador(trabajador.Id, _tipoApto.Id, fechaEmision, null, "archivo.pdf");
        _dbContext.Documentos.Add(documento);
        await _dbContext.SaveChangesAsync();

        var extraido = new MetadatosDocumentoExtraidosDto("Apto médico", fechaEmision, null, true, 99, null);
        var servicio = CrearServicio(new ExtraccionIaFalsa(Result.Exito(extraido)));

        await servicio.ProcesarDocumentoAsync(documento.Id);

        var revisiones = await _dbContext.RevisionesIaDocumento.Where(r => r.DocumentoId == documento.Id).ToListAsync();
        revisiones.Should().BeEmpty();

        var aprobaciones = await _dbContext.AprobacionesDocumento.Where(a => a.DocumentoId == documento.Id).ToListAsync();
        aprobaciones.Should().ContainSingle();
        aprobaciones[0].Tipo.Should().Be(TipoAprobacionDocumento.Automatica);
        aprobaciones[0].ConfianzaGeneral.Should().Be(99);
        aprobaciones[0].UsuarioId.Should().BeNull();
    }

    [Fact]
    public async Task No_hace_nada_si_el_tipo_de_documento_no_tiene_verificacion_activa()
    {
        _tipoApto.EstablecerVerificacionIaActiva(false);
        await _dbContext.SaveChangesAsync();

        var trabajador = CrearTrabajador();
        _dbContext.Trabajadores.Add(trabajador);
        var documento = Documento.DeTrabajador(trabajador.Id, _tipoApto.Id, DateOnly.FromDateTime(DateTime.UtcNow), null, "archivo.pdf");
        _dbContext.Documentos.Add(documento);
        await _dbContext.SaveChangesAsync();

        var servicioLlamado = false;
        var servicio = CrearServicio(new ExtraccionIaFalsaConSenal(() => servicioLlamado = true));

        await servicio.ProcesarDocumentoAsync(documento.Id);

        servicioLlamado.Should().BeFalse();
        (await _dbContext.RevisionesIaDocumento.AnyAsync(r => r.DocumentoId == documento.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task No_duplica_si_ya_hay_una_revision_pendiente_para_el_mismo_documento()
    {
        var trabajador = CrearTrabajador();
        _dbContext.Trabajadores.Add(trabajador);
        var fechaEmision = DateOnly.FromDateTime(DateTime.UtcNow);
        var documento = Documento.DeTrabajador(trabajador.Id, _tipoApto.Id, fechaEmision, null, "archivo.pdf");
        _dbContext.Documentos.Add(documento);
        _dbContext.RevisionesIaDocumento.Add(RevisionIaDocumento.Crear(documento.Id, 50, null, null, null, null, "Confianza baja (50%)"));
        await _dbContext.SaveChangesAsync();

        var servicioLlamado = false;
        var servicio = CrearServicio(new ExtraccionIaFalsaConSenal(() => servicioLlamado = true));

        await servicio.ProcesarDocumentoAsync(documento.Id);

        servicioLlamado.Should().BeFalse();
        (await _dbContext.RevisionesIaDocumento.CountAsync(r => r.DocumentoId == documento.Id)).Should().Be(1);
    }

    private sealed class ExtraccionIaFalsa(Result<MetadatosDocumentoExtraidosDto> resultado) : IExtraccionMetadatosDocumentoIaService
    {
        public Task<Result<MetadatosDocumentoExtraidosDto>> ExtraerAsync(byte[] contenidoPdf, string nombreTipoDocumento, CancellationToken cancellationToken = default) =>
            Task.FromResult(resultado);
    }

    private sealed class ExtraccionIaFalsaConSenal(Action alLlamar) : IExtraccionMetadatosDocumentoIaService
    {
        public Task<Result<MetadatosDocumentoExtraidosDto>> ExtraerAsync(byte[] contenidoPdf, string nombreTipoDocumento, CancellationToken cancellationToken = default)
        {
            alLlamar();
            return Task.FromResult(Result.Fallo<MetadatosDocumentoExtraidosDto>(Error.Crear("Test.NoLlamar", "No debería llamarse.")));
        }
    }

    private sealed class AlmacenamientoFalso : IFileStorageService
    {
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            Task.FromResult("falso.pdf");

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream([1, 2, 3]));
    }
}
