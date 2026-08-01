using CaeManager.Infrastructure.FileStorage;
using CaeManager.Infrastructure.MultiTenancy;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
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
///
/// P1-12 de docs/business/MATURITY_REVIEW.md añade el cifrado en reposo: las
/// mismas garantías de aislamiento de arriba, más que el contenido en disco
/// nunca sea el texto plano y que un archivo legado (guardado antes de este
/// cambio) se siga sirviendo sin romper la descarga.
/// </summary>
public class DiskFileStorageServiceTests : IDisposable
{
    private readonly string _rutaTemporal = Path.Combine(Path.GetTempPath(), $"caemanager-filestorage-{Guid.NewGuid():N}");
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();

    // Una única instancia para toda la clase: el protector real de EF
    // Core/ASP.NET Core deriva su clave de las claves de Data Protection
    // persistidas — dos instancias distintas de EphemeralDataProtectionProvider
    // no comparten material, así que dos servicios que deban descifrarse
    // entre sí (mismo test) tienen que compartir proveedor.
    private readonly IDataProtectionProvider _dataProtectionProvider = new EphemeralDataProtectionProvider();

    public void Dispose()
    {
        if (Directory.Exists(_rutaTemporal)) Directory.Delete(_rutaTemporal, recursive: true);
    }

    private DiskFileStorageService CrearServicio(Guid? tenantId) =>
        new(
            Options.Create(new DiskFileStorageServiceOptions { Ruta = _rutaTemporal }),
            new EntornoDePruebaFalso(),
            new TenantActualAmbiental { TenantId = tenantId },
            _dataProtectionProvider);

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
    public async Task El_tenant_que_guardo_el_archivo_puede_volver_a_abrirlo_y_recupera_el_mismo_contenido()
    {
        var servicio = CrearServicio(_tenantA);
        using var contenido = new MemoryStream("contenido de prueba"u8.ToArray());
        var identificador = await servicio.GuardarAsync(contenido, "documento.pdf");

        await using var flujo = await servicio.AbrirAsync(identificador);
        using var lector = new StreamReader(flujo);

        (await lector.ReadToEndAsync()).Should().Be("contenido de prueba");
    }

    [Fact]
    public async Task El_contenido_en_disco_nunca_es_el_texto_plano()
    {
        // P1-12: el dato más sensible del sistema (PDFs de reconocimientos
        // médicos) no puede quedar legible con solo acceso al volumen.
        var servicio = CrearServicio(_tenantA);
        using var contenido = new MemoryStream("un dato de salud confidencial"u8.ToArray());

        var identificador = await servicio.GuardarAsync(contenido, "documento.pdf");
        var rutaCompleta = Path.Combine(_rutaTemporal, _tenantA.ToString("N"), Path.GetFileName(identificador));
        var bytesEnDisco = await File.ReadAllBytesAsync(rutaCompleta);

        System.Text.Encoding.UTF8.GetString(bytesEnDisco).Should().NotContain("un dato de salud confidencial");
    }

    [Fact]
    public async Task Un_archivo_legado_guardado_en_claro_antes_del_cifrado_se_sigue_sirviendo()
    {
        // No hay migración automática de lo ya escrito (ver comentario de
        // clase de DiskFileStorageService) — un archivo legado tiene que
        // seguir descargándose, no romper con un error de descifrado.
        var carpetaTenant = _tenantA.ToString("N");
        Directory.CreateDirectory(Path.Combine(_rutaTemporal, carpetaTenant));
        var nombreArchivo = $"{Guid.NewGuid():N}.pdf";
        await File.WriteAllTextAsync(
            Path.Combine(_rutaTemporal, carpetaTenant, nombreArchivo), "documento legado sin cifrar");

        var servicio = CrearServicio(_tenantA);
        await using var flujo = await servicio.AbrirAsync($"{carpetaTenant}/{nombreArchivo}");
        using var lector = new StreamReader(flujo);

        (await lector.ReadToEndAsync()).Should().Be("documento legado sin cifrar");
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
