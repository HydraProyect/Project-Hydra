using CaeManager.Infrastructure.FileStorage;
using CaeManager.Infrastructure.MultiTenancy;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// Etapa 4 de PLAN-MIGRACION-MULTITENANT.md: el almacenamiento de archivos
/// particionado por tenant es tan crítico para el aislamiento como el filtro
/// de EF Core — un documento (PDF de un Trabajador, categoría de salud) no
/// debe poder abrirse nunca desde otro tenant, aunque alguien adivine o
/// reutilice el identificador.
/// </summary>
public class DiskFileStorageServiceTests : IDisposable
{
    private readonly string _rutaTemporal = Path.Combine(Path.GetTempPath(), $"caemanager-filestorage-{Guid.NewGuid():N}");
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();

    public void Dispose()
    {
        if (Directory.Exists(_rutaTemporal)) Directory.Delete(_rutaTemporal, recursive: true);
    }

    private DiskFileStorageService CrearServicio(Guid? tenantId) =>
        new(
            Options.Create(new DiskFileStorageServiceOptions { Ruta = _rutaTemporal }),
            new EntornoDePruebaFalso(),
            new TenantActualAmbiental { TenantId = tenantId });

    [Fact]
    public async Task Guarda_el_archivo_bajo_una_carpeta_propia_del_tenant()
    {
        var servicio = CrearServicio(_tenantA);
        using var contenido = new MemoryStream("contenido de prueba"u8.ToArray());

        var identificador = await servicio.GuardarAsync(contenido, "documento.pdf");

        identificador.Should().StartWith(_tenantA.ToString("N") + "/");
        File.Exists(Path.Combine(_rutaTemporal, _tenantA.ToString("N"), Path.GetFileName(identificador))).Should().BeTrue();
    }

    [Fact]
    public async Task El_tenant_que_guardo_el_archivo_puede_volver_a_abrirlo()
    {
        var servicio = CrearServicio(_tenantA);
        using var contenido = new MemoryStream("contenido de prueba"u8.ToArray());
        var identificador = await servicio.GuardarAsync(contenido, "documento.pdf");

        await using var flujo = await servicio.AbrirAsync(identificador);

        flujo.Should().NotBeNull();
    }

    [Fact]
    public async Task Otro_tenant_no_puede_abrir_el_archivo_aunque_conozca_el_identificador_exacto()
    {
        var servicioA = CrearServicio(_tenantA);
        using var contenido = new MemoryStream("contenido de prueba"u8.ToArray());
        var identificador = await servicioA.GuardarAsync(contenido, "documento.pdf");

        var servicioB = CrearServicio(_tenantB);

        var accion = async () => await servicioB.AbrirAsync(identificador);

        // Igual que el fix IDOR del Issue #18: un identificador ajeno se
        // comporta exactamente como "no existe", nunca revela que pertenece
        // a otro tenant.
        await accion.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task No_se_puede_guardar_un_archivo_sin_tenant_resuelto()
    {
        var servicio = CrearServicio(tenantId: null);
        using var contenido = new MemoryStream("contenido de prueba"u8.ToArray());

        var accion = async () => await servicio.GuardarAsync(contenido, "documento.pdf");

        await accion.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task No_se_puede_abrir_nada_sin_tenant_resuelto()
    {
        var servicioA = CrearServicio(_tenantA);
        using var contenido = new MemoryStream("contenido de prueba"u8.ToArray());
        var identificador = await servicioA.GuardarAsync(contenido, "documento.pdf");

        var servicioSinTenant = CrearServicio(tenantId: null);
        var accion = async () => await servicioSinTenant.AbrirAsync(identificador);

        await accion.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task Un_identificador_con_intento_de_path_traversal_no_escapa_de_la_carpeta_del_tenant()
    {
        var servicio = CrearServicio(_tenantA);

        var accion = async () => await servicio.AbrirAsync($"{_tenantA:N}/../../../etc/passwd");

        await accion.Should().ThrowAsync<FileNotFoundException>();
    }

    private sealed class EntornoDePruebaFalso : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "CaeManager.IntegrationTests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
