using CaeManager.Application.Common;
using CaeManager.Infrastructure.FileStorage;
using CaeManager.Infrastructure.MultiTenancy;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

    private readonly AlertaOperativaFalsa _alertas = new();
    private readonly LoggerEspia _logger = new();

    private DiskFileStorageService CrearServicio(Guid? tenantId) =>
        new(
            Options.Create(new DiskFileStorageServiceOptions { Ruta = _rutaTemporal }),
            new EntornoDePruebaFalso(),
            new TenantActualAmbiental { TenantId = tenantId },
            _dataProtectionProvider,
            _alertas,
            _logger);

    private string RutaDe(Guid tenantId, string identificador) =>
        Path.Combine(_rutaTemporal, tenantId.ToString("N"), Path.GetFileName(identificador));

    /// <summary>
    /// Escribe un archivo con el formato ANTERIOR al versionado: payload de
    /// Data Protection del protector global, sin marca delante. Es lo que hay
    /// hoy en produccion, y tiene que seguir leyendose.
    /// </summary>
    private async Task<string> EscribirArchivoDeFormatoAnteriorAsync(Guid tenantId, string contenido)
    {
        var carpeta = tenantId.ToString("N");
        Directory.CreateDirectory(Path.Combine(_rutaTemporal, carpeta));
        var nombreArchivo = $"{Guid.NewGuid():N}.pdf";

        var protectorAnterior = _dataProtectionProvider.CreateProtector("CaeManager.Archivos.v1");
        var cifrado = protectorAnterior.Protect(System.Text.Encoding.UTF8.GetBytes(contenido));
        await File.WriteAllBytesAsync(Path.Combine(_rutaTemporal, carpeta, nombreArchivo), cifrado);

        return $"{carpeta}/{nombreArchivo}";
    }

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
    public async Task Guardar_no_deja_ficheros_temporales_junto_al_definitivo()
    {
        // GuardarAsync escribe primero a un temporal y lo publica con
        // File.Move: la carpeta del tenant no debe conservar el temporal
        // una vez terminada la operación, ni aunque la operación falle antes
        // de empezar a escribir (ver el test de cancelación).
        var servicio = CrearServicio(_tenantA);
        using var contenido = new MemoryStream("contenido de prueba"u8.ToArray());

        await servicio.GuardarAsync(contenido, "documento.pdf");

        var ficheros = Directory.GetFiles(Path.Combine(_rutaTemporal, _tenantA.ToString("N")));
        ficheros.Should().ContainSingle()
            .Which.Should().NotContain(".tmp-", "el temporal se renombra al final, nunca queda junto al definitivo");
    }

    [Fact]
    public async Task Cancelar_el_guardado_no_deja_ni_temporal_ni_definitivo_en_disco()
    {
        var servicio = CrearServicio(_tenantA);
        using var contenido = new MemoryStream("contenido de prueba"u8.ToArray());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var accion = async () => await servicio.GuardarAsync(contenido, "documento.pdf", cts.Token);

        await accion.Should().ThrowAsync<OperationCanceledException>();

        var carpeta = Path.Combine(_rutaTemporal, _tenantA.ToString("N"));
        if (Directory.Exists(carpeta))
            Directory.GetFiles(carpeta).Should().BeEmpty("una escritura cancelada no debe dejar rastro, ni temporal ni definitivo");
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

    [Fact]
    public async Task Un_archivo_del_formato_anterior_cifrado_y_sin_marca_se_sigue_leyendo()
    {
        // EL caso que no se puede romper: es lo que hay escrito en produccion
        // ahora mismo. Si el formato versionado dejara de entenderlo, cada PDF
        // ya subido se volveria ilegible.
        var identificador = await EscribirArchivoDeFormatoAnteriorAsync(_tenantA, "informe medico anterior");

        var servicio = CrearServicio(_tenantA);
        await using var flujo = await servicio.AbrirAsync(identificador);
        using var lector = new StreamReader(flujo);

        (await lector.ReadToEndAsync()).Should().Be("informe medico anterior");
    }

    [Fact]
    public async Task Un_archivo_cifrado_manipulado_no_se_sirve()
    {
        // Antes del formato versionado, alterar el ciphertext hacia fallar a
        // Unprotect, ese fallo se interpretaba como "archivo legado en claro"
        // y los bytes manipulados se entregaban como contenido legitimo. La
        // marca deshace la ambiguedad: si dice que esta cifrado, un descifrado
        // fallido es integridad rota y no se sirve nada.
        var servicio = CrearServicio(_tenantA);
        using var contenido = new MemoryStream("un dato de salud confidencial"u8.ToArray());
        var identificador = await servicio.GuardarAsync(contenido, "documento.pdf");

        var ruta = RutaDe(_tenantA, identificador);
        var bytes = await File.ReadAllBytesAsync(ruta);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(ruta, bytes);

        var accion = async () => await servicio.AbrirAsync(identificador);

        await accion.Should().ThrowAsync<InvalidDataException>();
        _alertas.Alertas.Should().ContainSingle()
            .Which.Nivel.Should().Be(NivelAlertaOperativa.Critica,
                "manipulacion o perdida de claves es un incidente, no un error de usuario");
    }

    [Fact]
    public async Task El_archivo_de_un_tenant_no_se_descifra_con_la_clave_de_otro()
    {
        // Aislamiento criptografico, no solo de ruta: se copia el fichero de A
        // dentro de la carpeta de B, de modo que la comprobacion de ruta pasa
        // y lo unico que puede impedir la lectura es que la clave se derive
        // del tenant.
        var servicioA = CrearServicio(_tenantA);
        using var contenido = new MemoryStream("un dato de salud confidencial"u8.ToArray());
        var identificadorA = await servicioA.GuardarAsync(contenido, "documento.pdf");

        var carpetaB = _tenantB.ToString("N");
        Directory.CreateDirectory(Path.Combine(_rutaTemporal, carpetaB));
        var nombreArchivo = Path.GetFileName(identificadorA);
        File.Copy(RutaDe(_tenantA, identificadorA), Path.Combine(_rutaTemporal, carpetaB, nombreArchivo));

        var servicioB = CrearServicio(_tenantB);
        var accion = async () => await servicioB.AbrirAsync($"{carpetaB}/{nombreArchivo}");

        await accion.Should().ThrowAsync<InvalidDataException>(
            "la ruta cuadra, asi que si se leyera seria porque la clave no depende del tenant");
    }

    [Fact]
    public async Task Leer_un_archivo_legado_cifrado_sin_marca_deja_constancia_de_que_queda_formato_anterior()
    {
        // La razón de ser de este test: sin él, la única población legada que
        // de verdad puede haber en producción se servía SIN dejar rastro. El
        // primer despliegue real fue el 2026-08-24 y el cifrado v1 entró el
        // 2026-08-01, así que lo que hay allí está cifrado-sin-marca, no en
        // claro. Un registro sin avisos se leería entonces como "ya no queda
        // nada legado" justo cuando queda todo, y retirar la rama legada por
        // esa lectura dejaría ilegible cada PDF subido antes del formato v2.
        var identificador = await EscribirArchivoDeFormatoAnteriorAsync(_tenantA, "informe medico anterior");

        var servicio = CrearServicio(_tenantA);
        await using var flujo = await servicio.AbrirAsync(identificador);

        _logger.Avisos.Should().ContainSingle(aviso => aviso.Contains("protector v1"),
            "es el único instrumento que puede responder si queda formato anterior en producción");
    }

    [Fact]
    public async Task Leer_un_archivo_legado_en_claro_deja_constancia_de_que_queda_formato_anterior()
    {
        var carpetaTenant = _tenantA.ToString("N");
        Directory.CreateDirectory(Path.Combine(_rutaTemporal, carpetaTenant));
        var nombreArchivo = $"{Guid.NewGuid():N}.pdf";
        await File.WriteAllTextAsync(
            Path.Combine(_rutaTemporal, carpetaTenant, nombreArchivo), "documento legado sin cifrar");

        var servicio = CrearServicio(_tenantA);
        await using var flujo = await servicio.AbrirAsync($"{carpetaTenant}/{nombreArchivo}");

        _logger.Avisos.Should().ContainSingle(aviso => aviso.Contains("sin cifrar"));
    }

    [Fact]
    public async Task Leer_un_archivo_del_formato_actual_no_deja_ningun_aviso_de_formato_anterior()
    {
        // La otra mitad de la medición: si el aviso saltara también con v2, el
        // recuento en producción no distinguiría lo legado de lo corriente y
        // dejaría de poder responder la pregunta.
        var servicio = CrearServicio(_tenantA);
        using var contenido = new MemoryStream("un dato de salud confidencial"u8.ToArray());
        var identificador = await servicio.GuardarAsync(contenido, "documento.pdf");

        await using var flujo = await servicio.AbrirAsync(identificador);

        _logger.Avisos.Should().BeEmpty();
    }

    private sealed class LoggerEspia : ILogger<DiskFileStorageService>
    {
        public List<string> Avisos { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) Avisos.Add(formatter(state, exception));
        }
    }

    private sealed class AlertaOperativaFalsa : IAlertaOperativa
    {
        public List<(string Mensaje, NivelAlertaOperativa Nivel)> Alertas { get; } = [];

        public void Emitir(string mensaje, NivelAlertaOperativa nivel) => Alertas.Add((mensaje, nivel));

        public void CapturarExcepcion(Exception excepcion) { }

        public void DejarMigaDePan(string mensaje) { }

        public IDisposable IniciarAmbitoDeCaptura() => new AmbitoVacio();

        private sealed class AmbitoVacio : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class EntornoDePruebaFalso : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "CaeManager.IntegrationTests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
