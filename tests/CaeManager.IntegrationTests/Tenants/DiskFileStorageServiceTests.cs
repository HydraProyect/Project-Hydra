using CaeManager.Application.Common;
using CaeManager.Application.ContratosAcceso.LecturaInbound;
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
/// Etapa 4 de Project-Hydra-Negocio/tecnico/PLAN-MIGRACION-MULTITENANT.md: el almacenamiento de archivos
/// particionado por tenant es tan crítico para el aislamiento como el filtro
/// de EF Core — un documento (PDF de un Trabajador, categoría de salud) no
/// debe poder abrirse nunca desde otro tenant, aunque alguien adivine o
/// reutilice el identificador.
///
/// P1-12 de Project-Hydra-Negocio/MATURITY_REVIEW.md añade el cifrado en reposo: las
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

    private DiskFileStorageService CrearServicio(Guid? tenantId, bool rechazarLegadoEnClaro = false) =>
        new(
            Options.Create(new DiskFileStorageServiceOptions { Ruta = _rutaTemporal, RechazarLegadoEnClaro = rechazarLegadoEnClaro }),
            new EntornoDePruebaFalso(),
            new TenantActualAmbiental { TenantId = tenantId },
            _dataProtectionProvider,
            _alertas,
            _logger);

    private string RutaDe(Guid tenantId, string identificador) =>
        Path.Combine(_rutaTemporal, tenantId.ToString("N"), Path.GetFileName(identificador));

    [Fact]
    public void La_huella_es_determinista_y_corresponde_al_nombre_en_disco()
    {
        const string nombre = "Ana Garcia - reconocimiento medico.pdf";
        DiskFileStorageService.HuellaArchivo($"{_tenantA:N}/{nombre}").Should().Be("c1b147d77066");
        DiskFileStorageService.HuellaArchivo($"{_tenantB:N}/{nombre}").Should().Be("c1b147d77066");
        DiskFileStorageService.HuellaArchivo($"{_tenantA:N}/otro.pdf").Should().NotBe("c1b147d77066");
    }

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

    [Theory]
    [InlineData("documento.PDF", ".pdf")]
    [InlineData("documento..JpEg", ".jpeg")]
    [InlineData("documento", "")]
    [InlineData("documento.PDF.tmp-0123456789abcdef0123456789abcdef", ".pdf")]
    [InlineData("documento.PDF.TMP-0123456789ABCDEF0123456789ABCDEF.tmp-1", ".pdf")]
    [InlineData("documento.tmp-0123456789abcdef0123456789abcdef", "")]
    public async Task El_definitivo_conserva_la_extension_normalizada_sin_sufijos_temporales(string original, string extension)
    {
        var servicio = CrearServicio(_tenantA);
        using var contenido = new MemoryStream("contenido"u8.ToArray());

        var identificador = await servicio.GuardarAsync(contenido, original);

        var nombre = Path.GetFileName(identificador);
        nombre.Should().NotContain(".tmp-").And.NotContain("..");
        Path.GetExtension(nombre).Should().Be(extension);
        nombre.Length.Should().Be(32 + extension.Length);
        Directory.GetFiles(Path.Combine(_rutaTemporal, _tenantA.ToString("N")))
            .Should().ContainSingle().Which.Should().Be(RutaDe(_tenantA, identificador));
        await using var abierto = await servicio.AbrirAsync(identificador);
        abierto.Length.Should().Be(contenido.Length);
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

    /// <summary>
    /// Inbound M1, PR 4: la única lectura que cruza Tenants abre el original con la carpeta y la clave del Tenant
    /// propietario que se le pasa (el de la Empresa contratista del contrato), no con las del Tenant activo, y la copia
    /// que se guarda después queda en la carpeta y con la clave del Tenant activo.
    /// </summary>
    [Fact]
    public async Task AbrirOriginal_lee_con_la_carpeta_y_la_clave_del_Tenant_propietario_indicado()
    {
        using var original = new MemoryStream("original del Tenant B"u8.ToArray());
        var deB = await CrearServicio(_tenantB).GuardarAsync(original, "aptitud.pdf");
        var servicioDeA = CrearServicio(_tenantA);

        await using (var abierto = await servicioDeA.AbrirOriginalAsync(new ArchivoInboundDto(Guid.NewGuid(), _tenantB, deB, false)))
        {
            using var lectura = new StreamReader(abierto);
            (await lectura.ReadToEndAsync()).Should().Be("original del Tenant B");
        }

        await FluentActions.Awaiting(() => servicioDeA.AbrirAsync(deB))
            .Should().ThrowAsync<FileNotFoundException>("control: con el Tenant activo sigue sin abrirse");
        var copia = await servicioDeA.GuardarAsync(new MemoryStream("copia"u8.ToArray()), "aptitud.pdf");
        copia.Should().StartWith(_tenantA.ToString("N") + "/", "la copia es del Tenant activo, el del Cliente empresarial");
    }

    [Fact]
    public async Task AbrirOriginal_con_un_Tenant_que_no_es_el_de_la_carpeta_responde_no_existe()
    {
        using var original = new MemoryStream("original del Tenant B"u8.ToArray());
        var deB = await CrearServicio(_tenantB).GuardarAsync(original, "aptitud.pdf");
        var servicioDeA = CrearServicio(_tenantA);

        foreach (var (tenant, identificador, motivo) in new[]
                 {
                     (_tenantA, deB, "la carpeta del identificador es la de B, no la del Tenant indicado"),
                     (Guid.NewGuid(), deB, "un tercer Tenant"),
                     (Guid.Empty, deB, "sin Tenant propietario"),
                     (_tenantB, $"{_tenantB:N}/../{_tenantA:N}/x.pdf", "un salto de carpeta"),
                     (_tenantB, Path.GetFileName(deB), "sin carpeta"),
                 })
        {
            await FluentActions.Awaiting(() => servicioDeA.AbrirOriginalAsync(new ArchivoInboundDto(Guid.NewGuid(), tenant, identificador, false)))
                .Should().ThrowAsync<FileNotFoundException>(motivo);
        }
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
    public async Task Un_fallo_de_escritura_antes_del_rename_no_publica_el_definitivo()
    {
        var servicio = CrearServicio(_tenantA);
        using var cts = new CancellationTokenSource();
        using var contenido = new FlujoQueCancelaTrasCopiar(cts);

        await FluentActions.Awaiting(() => servicio.GuardarAsync(contenido, "documento.PDF", cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        contenido.Copiado.Should().BeTrue("el fallo llega después de leer el contenido y antes del rename");
        Directory.GetFiles(Path.Combine(_rutaTemporal, _tenantA.ToString("N"))).Should().BeEmpty();
    }

    private sealed class FlujoQueCancelaTrasCopiar(CancellationTokenSource cts) : MemoryStream("contenido"u8.ToArray())
    {
        public bool Copiado { get; private set; }

        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            await base.CopyToAsync(destination, bufferSize, cancellationToken);
            Copiado = true;
            cts.Cancel();
        }
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

        // NOTA DE ALCANCE (medida, no supuesta): este test cancela ANTES de
        // que GuardarAsync arranque, así que no ejercita "escritura
        // interrumpida a mitad de camino" — un probe aparte midió que
        // File.WriteAllBytesAsync con un CancellationToken cancelado a los
        // 5 ms de una escritura de 300 MB TERMINA la escritura igual, sin
        // lanzar, en 316 ms: el token no aborta una escritura ya en marcha en
        // esta plataforma, solo impide que arranque si ya estaba cancelado
        // al llamar. Eso hace que el escenario real que motiva esta defensa
        // —un proceso interrumpido a mitad de escribir (OOM, kill -9, caída
        // del host)— no sea reproducible con CancellationToken en el mismo
        // proceso: hace falta matar un proceso de verdad mientras escribe,
        // que es una carga de arnés desproporcionada para esta rama. Queda
        // como hueco medido, no como propiedad demostrada.
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
        _alertas.Alertas[0].Mensaje.Should().Contain(_tenantA.ToString("N"))
            .And.NotContain(Path.GetFileName(identificador));
        _logger.Avisos.Should().ContainSingle().Which.Should().Contain(_tenantA.ToString("N"))
            .And.Contain(DiskFileStorageService.HuellaArchivo(identificador))
            .And.NotContain(Path.GetFileName(identificador));
    }

    [Fact]
    public async Task La_alerta_v2_es_estable_por_Tenant_propietario_incluso_en_lectura_Inbound()
    {
        var servicioA = CrearServicio(_tenantA);
        var servicioB = CrearServicio(_tenantB);
        var identificadores = new List<string>();
        foreach (var nombre in new[] { "uno.pdf", "dos.pdf" })
        {
            using var contenido = new MemoryStream("contenido"u8.ToArray());
            var identificador = await servicioA.GuardarAsync(contenido, nombre);
            identificadores.Add(identificador);
            var ruta = RutaDe(_tenantA, identificador);
            var bytes = await File.ReadAllBytesAsync(ruta);
            bytes[^1] ^= 0xFF;
            await File.WriteAllBytesAsync(ruta, bytes);
            var accion = async () => await servicioB.AbrirOriginalAsync(new ArchivoInboundDto(Guid.NewGuid(), _tenantA, identificador, false));
            await accion.Should().ThrowAsync<InvalidDataException>();
        }

        _alertas.Alertas.Should().HaveCount(2);
        _alertas.Alertas.Select(a => a.Mensaje).Distinct().Should().ContainSingle()
            .Which.Should().Contain(_tenantA.ToString("N")).And.NotContain(_tenantB.ToString("N"));
        for (var i = 0; i < identificadores.Count; i++)
        {
            var nombre = Path.GetFileName(identificadores[i]);
            _alertas.Alertas[i].Mensaje.Should().NotContain(nombre);
            _logger.Avisos[i].Should().Contain(_tenantA.ToString("N"))
                .And.NotContain(_tenantB.ToString("N"))
                .And.Contain(DiskFileStorageService.HuellaArchivo(identificadores[i]))
                .And.NotContain(nombre);
        }
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
        _logger.Avisos[0].Should().Contain(_tenantA.ToString("N"))
            .And.Contain(DiskFileStorageService.HuellaArchivo(identificador))
            .And.NotContain(Path.GetFileName(identificador));
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

        _logger.Avisos.Should().ContainSingle(aviso => aviso.Contains("sin cifrar"))
            .Which.Should().Contain(carpetaTenant)
            .And.Contain(DiskFileStorageService.HuellaArchivo($"{carpetaTenant}/{nombreArchivo}"))
            .And.NotContain(nombreArchivo);
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

    /// <summary>
    /// Escribe un archivo sin marca v2 ni payload v1: lo mismo que un legado anterior al cifrado en reposo, y lo
    /// mismo que dejaría quien escribiera directamente en el volumen de almacenamiento.
    /// </summary>
    private async Task<string> EscribirArchivoEnClaroAsync(Guid tenantId, string contenido, string? nombreArchivo = null)
    {
        var carpeta = tenantId.ToString("N");
        Directory.CreateDirectory(Path.Combine(_rutaTemporal, carpeta));
        nombreArchivo ??= $"{Guid.NewGuid():N}.pdf";
        await File.WriteAllTextAsync(Path.Combine(_rutaTemporal, carpeta, nombreArchivo), contenido);

        return $"{carpeta}/{nombreArchivo}";
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Ana Garcia - reconocimiento medico.pdf")]
    public async Task Servir_un_archivo_sin_cifrar_ni_marca_emite_una_alerta_critica_sin_datos_personales(string? nombreArchivo)
    {
        // S-D-1: antes, esta rama solo dejaba un LogWarning, mientras que el v2
        // que no supera la verificación emite alerta crítica. Un fichero en
        // claro no se distingue de uno escrito por quien tenga acceso al
        // volumen, así que tiene que llegar a la guardia por el mismo canal.
        // La alerta no lleva el nombre de archivo: ni el que generó el almacén
        // (su extensión viene del original) ni, menos aún, uno puesto a mano.
        var identificador = await EscribirArchivoEnClaroAsync(_tenantA, "contenido", nombreArchivo);
        var nombreEnDisco = Path.GetFileName(identificador);

        var servicio = CrearServicio(_tenantA);
        await using var flujo = await servicio.AbrirAsync(identificador);

        var alerta = _alertas.Alertas.Should().ContainSingle().Subject;
        alerta.Nivel.Should().Be(NivelAlertaOperativa.Critica);
        alerta.Mensaje.Should().Contain(_tenantA.ToString("N"), "la guardia necesita saber de qué Tenant propietario es")
            .And.NotContain(nombreEnDisco)
            .And.NotContain(Path.GetFileNameWithoutExtension(nombreEnDisco));
        _logger.Avisos.Should().ContainSingle().Which.Should().Contain(_tenantA.ToString("N"))
            .And.Contain(DiskFileStorageService.HuellaArchivo(identificador))
            .And.NotContain(nombreEnDisco);
    }

    [Fact]
    public async Task La_alerta_es_la_misma_para_todos_los_archivos_en_claro_de_un_Tenant()
    {
        // Una población legada no puede abrir una incidencia por archivo en la
        // guardia: el mensaje estable agrupa por Tenant propietario.
        var primero = await EscribirArchivoEnClaroAsync(_tenantA, "uno");
        var segundo = await EscribirArchivoEnClaroAsync(_tenantA, "dos");

        var servicio = CrearServicio(_tenantA);
        await using (await servicio.AbrirAsync(primero)) { }
        await using (await servicio.AbrirAsync(segundo)) { }

        _alertas.Alertas.Should().HaveCount(2);
        _alertas.Alertas.Select(a => a.Mensaje).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task Con_el_rechazo_activo_un_archivo_sin_cifrar_ni_marca_no_se_sirve_y_alerta()
    {
        var identificador = await EscribirArchivoEnClaroAsync(_tenantA, "contenido no autenticado");

        var servicio = CrearServicio(_tenantA, rechazarLegadoEnClaro: true);
        var accion = async () => await servicio.AbrirAsync(identificador);

        await accion.Should().ThrowAsync<InvalidDataException>("falla cerrado, igual que un v2 que no supera la verificación");
        var alerta = _alertas.Alertas.Should().ContainSingle().Subject;
        alerta.Nivel.Should().Be(NivelAlertaOperativa.Critica);
        alerta.Mensaje.Should().Contain(_tenantA.ToString("N")).And.Contain("rechazó");
        _logger.Avisos.Should().ContainSingle().Which.Should().Contain(_tenantA.ToString("N"))
            .And.Contain(DiskFileStorageService.HuellaArchivo(identificador))
            .And.NotContain(Path.GetFileName(identificador));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Un_original_Inbound_sin_cifrar_ni_marca_alerta_con_el_Tenant_propietario_y_no_con_el_lector(bool rechazarLegadoEnClaro)
    {
        // La lectura que cruza Tenants pasa por el mismo descifrado, con la
        // clave y la carpeta del Tenant propietario.
        var identificador = await EscribirArchivoEnClaroAsync(_tenantA, "contenido no autenticado");

        var servicioB = CrearServicio(_tenantB, rechazarLegadoEnClaro);
        var accion = async () => await servicioB.AbrirOriginalAsync(new ArchivoInboundDto(Guid.NewGuid(), _tenantA, identificador, false));

        if (rechazarLegadoEnClaro)
        {
            await accion.Should().ThrowAsync<InvalidDataException>();
        }
        else
        {
            await using var flujo = await accion();
            using var lector = new StreamReader(flujo);
            (await lector.ReadToEndAsync()).Should().Be("contenido no autenticado", "por defecto se sigue sirviendo");
        }

        _alertas.Alertas.Should().ContainSingle()
            .Which.Mensaje.Should().Contain(_tenantA.ToString("N")).And.NotContain(_tenantB.ToString("N"));
        _logger.Avisos.Should().ContainSingle().Which.Should().Contain(_tenantA.ToString("N"))
            .And.NotContain(_tenantB.ToString("N"))
            .And.Contain(DiskFileStorageService.HuellaArchivo(identificador))
            .And.NotContain(Path.GetFileName(identificador));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Los_formatos_v1_y_v2_se_siguen_leyendo_sin_alertas_con_y_sin_rechazo(bool rechazarLegadoEnClaro)
    {
        // El rechazo solo afecta a la rama en claro: un v1 sin marca se
        // descifra autenticado y un v2 lleva su marca, así que ninguno de los
        // dos es ambiguo ni debe alertar.
        var identificadorV1 = await EscribirArchivoDeFormatoAnteriorAsync(_tenantA, "informe medico anterior");
        var servicio = CrearServicio(_tenantA, rechazarLegadoEnClaro);
        using var contenido = new MemoryStream("un dato de salud confidencial"u8.ToArray());
        var identificadorV2 = await servicio.GuardarAsync(contenido, "documento.pdf");

        await using (var flujoV1 = await servicio.AbrirAsync(identificadorV1))
        using (var lectorV1 = new StreamReader(flujoV1))
            (await lectorV1.ReadToEndAsync()).Should().Be("informe medico anterior");

        await using (var flujoV2 = await servicio.AbrirAsync(identificadorV2))
        using (var lectorV2 = new StreamReader(flujoV2))
            (await lectorV2.ReadToEndAsync()).Should().Be("un dato de salud confidencial");

        _alertas.Alertas.Should().BeEmpty();
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
