using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores.Deteccion;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Notificaciones;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// DeteccionTrabajadoresService (Fase 36) depende de IApplicationDbContext
/// para comparar el listado extraído contra los Trabajadores reales de la
/// Empresa — se prueba contra SQLite real, mismo criterio que
/// AlcancePorIdTests/MigracionesTests. La lectura por IA en sí (Anthropic)
/// se sustituye por un fake determinista: lo que se prueba aquí es la
/// lógica de comparación y las condiciones de "no hacer nada", no la
/// llamada externa real.
/// </summary>
public class DeteccionTrabajadoresServiceTests : IAsyncLifetime
{
    private static readonly Guid IdTipoDocumentoIta = new("20000000-0000-0000-0000-000000000003");
    private static readonly Guid IdTipoDocumentoCertificadoSs = new("20000000-0000-0000-0000-000000000001");

    private readonly string _rutaBaseDatos = Path.Combine(Path.GetTempPath(), $"caemanager-tests-{Guid.NewGuid()}.db");
    private CaeManagerDbContext _dbContext = null!;

    public async Task InitializeAsync()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = TenantSeedData.IdPorDefecto };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseSqlite($"Data Source={_rutaBaseDatos}")
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        _dbContext = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
        await _dbContext.Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        _dbContext.Dispose();
        File.Delete(_rutaBaseDatos);
        return Task.CompletedTask;
    }

    private DeteccionTrabajadoresService CrearServicio(IExtraccionTrabajadoresIaService extraccion, IFileStorageService? almacenamiento = null) =>
        new(_dbContext, almacenamiento ?? new AlmacenamientoFalso(), extraccion,
            new DeteccionTrabajadorRepository(_dbContext), new NotificacionUsuarioRepository(_dbContext),
            _dbContext, Microsoft.Extensions.Logging.Abstractions.NullLogger<DeteccionTrabajadoresService>.Instance);

    [Fact]
    public async Task Detecta_altas_y_bajas_y_avisa_al_gestor_del_cliente()
    {
        var gestorId = Guid.NewGuid();
        var cliente = new Cliente("COBEGA", "B12345674", false, ejecutivoUsuarioId: gestorId);
        var empresa = new Empresa("KHS S.A.");
        _dbContext.Clientes.Add(cliente);
        _dbContext.Empresas.Add(empresa);
        _dbContext.EmpresasClientes.Add(new EmpresaCliente(empresa.Id, cliente.Id));

        var trabajadorQueSigue = Trabajador.DeEmpresa(empresa.Id, "Alvaro", "Sanchez Martin", "77189989B");
        var trabajadorQueYaNoAparece = Trabajador.DeEmpresa(empresa.Id, "Pedro", "Gomez Ruiz", "12345678Z");
        _dbContext.Trabajadores.AddRange(trabajadorQueSigue, trabajadorQueYaNoAparece);

        var documento = Documento.DeEmpresa(empresa.Id, IdTipoDocumentoIta, DateOnly.FromDateTime(DateTime.UtcNow), null, "archivo.pdf");
        _dbContext.Documentos.Add(documento);
        await _dbContext.SaveChangesAsync();

        var extraidos = new List<TrabajadorExtraidoDto>
        {
            new("Alvaro", "Sanchez Martin", trabajadorQueSigue.Dni),
            new("Nuevo", "Trabajador Detectado", "99999999R")
        };
        var servicio = CrearServicio(new ExtraccionIaFalsa(Result.Exito<IReadOnlyList<TrabajadorExtraidoDto>>(extraidos)));

        await servicio.ProcesarDocumentoAsync(documento.Id);

        var detecciones = await _dbContext.DeteccionesTrabajador.Where(d => d.DocumentoId == documento.Id).ToListAsync();
        detecciones.Should().HaveCount(2);
        detecciones.Should().Contain(d => d.Tipo == TipoDeteccion.Nuevo && d.Dni == "99999999R");
        detecciones.Should().Contain(d => d.Tipo == TipoDeteccion.Ausente && d.TrabajadorExistenteId == trabajadorQueYaNoAparece.Id);

        var notificaciones = await _dbContext.NotificacionesUsuario.Where(n => n.UsuarioDestinatarioId == gestorId).ToListAsync();
        notificaciones.Should().ContainSingle(n => n.UrlAccion == $"/empresas/{empresa.Id}/deteccion-trabajadores");
    }

    [Fact]
    public async Task No_hace_nada_si_el_tipo_de_documento_no_tiene_deteccion_activa()
    {
        var empresa = new Empresa("KHS S.A.");
        _dbContext.Empresas.Add(empresa);
        var documento = Documento.DeEmpresa(empresa.Id, IdTipoDocumentoCertificadoSs, DateOnly.FromDateTime(DateTime.UtcNow), null, "archivo.pdf");
        _dbContext.Documentos.Add(documento);
        await _dbContext.SaveChangesAsync();

        var servicio = CrearServicio(new ExtraccionIaFalsa(Result.Exito<IReadOnlyList<TrabajadorExtraidoDto>>([new TrabajadorExtraidoDto("X", "Y", "99999999R")])));

        await servicio.ProcesarDocumentoAsync(documento.Id);

        (await _dbContext.DeteccionesTrabajador.AnyAsync(d => d.DocumentoId == documento.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task No_compara_trabajadores_de_subcontrata()
    {
        var empresa = new Empresa("KHS S.A.");
        var subcontrata = new Subcontrata("Subcontrata de Limpieza");
        _dbContext.Empresas.Add(empresa);
        _dbContext.Subcontratas.Add(subcontrata);

        var trabajadorSubcontrata = Trabajador.DeSubcontrata(subcontrata.Id, "Juan", "Perez Lopez", "11111111H");
        _dbContext.Trabajadores.Add(trabajadorSubcontrata);

        var documento = Documento.DeEmpresa(empresa.Id, IdTipoDocumentoIta, DateOnly.FromDateTime(DateTime.UtcNow), null, "archivo.pdf");
        _dbContext.Documentos.Add(documento);
        await _dbContext.SaveChangesAsync();

        var servicio = CrearServicio(new ExtraccionIaFalsa(Result.Exito<IReadOnlyList<TrabajadorExtraidoDto>>([])));

        await servicio.ProcesarDocumentoAsync(documento.Id);

        // El trabajador de subcontrata no debe generar una detección de "ausente": no pertenece a esta Empresa.
        (await _dbContext.DeteccionesTrabajador.AnyAsync(d => d.DocumentoId == documento.Id)).Should().BeFalse();
    }

    private sealed class ExtraccionIaFalsa(Result<IReadOnlyList<TrabajadorExtraidoDto>> resultado) : IExtraccionTrabajadoresIaService
    {
        public Task<Result<IReadOnlyList<TrabajadorExtraidoDto>>> ExtraerAsync(byte[] contenidoPdf, CancellationToken cancellationToken = default) =>
            Task.FromResult(resultado);
    }

    private sealed class AlmacenamientoFalso : IFileStorageService
    {
        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            Task.FromResult("falso.pdf");

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream([1, 2, 3]));
    }
}
