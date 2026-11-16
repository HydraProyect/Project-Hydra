using CaeManager.Application.Exportacion;
using CaeManager.Infrastructure.FileStorage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Exportacion;

public class OpcionesAlmacenExportacionesCompletas
{
    public const string SeccionConfiguracion = "ExportacionesCompletas";

    /// <summary>
    /// Carpeta de los zips. Vacía: la carpeta <c>exportaciones</c> junto a la de Documentos
    /// (<see cref="DiskFileStorageServiceOptions.Ruta"/>), que en producción cae en el mismo volumen persistente
    /// que comparten las dos ranuras de despliegue.
    /// </summary>
    public string? Ruta { get; set; }

    /// <summary>
    /// Espacio libre mínimo del volumen del almacén para empezar a generar una exportación (OWASP API4:2023
    /// «Unrestricted Resource Consumption» y ASVS 4.0.3 V12.1 «File Upload», límites de tamaño y de espacio): por
    /// debajo, la solicitud falla con el motivo <c>SinEspacio</c> en vez de llenar el volumen que comparte con los
    /// Documentos. Por defecto 5 GiB, el margen para la exportación del Tenant más grande previsto más el temporal.
    /// Cero desactiva la comprobación.
    /// </summary>
    public long EspacioLibreMinimoBytes { get; set; } = EspacioLibreMinimoPorDefecto;

    public const long EspacioLibreMinimoPorDefecto = 5L * 1024 * 1024 * 1024;
}

/// <summary>
/// Los zips de exportación completa en disco, una carpeta por Tenant propietario. Se escriben en un temporal y se
/// renombran al terminar, así que la ruta final nunca existe a medias. Los ficheros ya van cifrados con la
/// contraseña de la exportación (AES-256 del zip); no se cifran otra vez. La ubicación que guarda la fila es
/// relativa (<c>TenantId/SolicitudId.zip</c>) y se comprueba al abrir: una ubicación de otro Tenant, o que salga
/// de su carpeta, no se abre.
/// <para>
/// Consumo acotado (OWASP API4:2023, ASVS 4.0.3 V12.1): por Tenant hay como mucho un zip listo (el generador
/// caduca el anterior al marcar uno nuevo) y el temporal del que se está generando; no se empieza ninguno sin
/// <see cref="OpcionesAlmacenExportacionesCompletas.EspacioLibreMinimoBytes"/> libres, y
/// <see cref="BarrerHuerfanos"/> recoge lo que quede de un intento roto.
/// </para>
/// </summary>
public class AlmacenExportacionesCompletasEnDisco : IAlmacenExportacionesCompletas
{
    private const string MarcaTemporal = ".tmp-";

    private readonly string _rutaBase;
    private readonly long _espacioLibreMinimoBytes;

    public AlmacenExportacionesCompletasEnDisco(
        IOptions<OpcionesAlmacenExportacionesCompletas> opciones,
        IOptions<DiskFileStorageServiceOptions> opcionesDocumentos,
        IHostEnvironment entorno)
    {
        var ruta = opciones.Value.Ruta;
        if (string.IsNullOrWhiteSpace(ruta))
        {
            var documentos = Path.IsPathRooted(opcionesDocumentos.Value.Ruta)
                ? opcionesDocumentos.Value.Ruta
                : Path.Combine(entorno.ContentRootPath, opcionesDocumentos.Value.Ruta);
            ruta = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(documentos)))!, "exportaciones");
        }

        _rutaBase = Path.GetFullPath(Path.IsPathRooted(ruta) ? ruta : Path.Combine(entorno.ContentRootPath, ruta));

        if (opciones.Value.EspacioLibreMinimoBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(opciones), "El espacio libre mínimo del almacén de exportaciones no puede ser negativo.");
        _espacioLibreMinimoBytes = opciones.Value.EspacioLibreMinimoBytes;
    }

    public long EspacioLibreMinimoBytes => _espacioLibreMinimoBytes;

    /// <summary>
    /// Si el volumen del almacén tiene al menos <see cref="OpcionesAlmacenExportacionesCompletas.EspacioLibreMinimoBytes"/>
    /// libres para empezar una exportación. Devuelve además lo que mide, para el registro.
    /// </summary>
    public bool HayEspacioParaGenerar(out long libreBytes)
    {
        libreBytes = _espacioLibreMinimoBytes == 0 ? long.MaxValue : EspacioLibreBytes();
        return libreBytes >= _espacioLibreMinimoBytes;
    }

    /// <summary>
    /// Bytes libres del volumen donde está la carpeta base. En Linux se mide la propia carpeta (statvfs), que da el
    /// volumen montado en ella aunque no sea la raíz; en Windows, su unidad.
    /// </summary>
    private long EspacioLibreBytes()
    {
        Directory.CreateDirectory(_rutaBase);
        var volumen = OperatingSystem.IsWindows() ? Path.GetPathRoot(_rutaBase)! : _rutaBase;
        return new DriveInfo(volumen).AvailableFreeSpace;
    }

    public static string UbicacionPara(Guid tenantId, Guid solicitudId) => $"{tenantId:N}/{solicitudId:N}.zip";

    public Task<Stream> AbrirAsync(Guid tenantId, string ubicacion, CancellationToken cancellationToken = default)
    {
        var ruta = RutaComprobada(tenantId, ubicacion);
        if (!File.Exists(ruta))
            throw new FileNotFoundException("La exportación no está en el almacén.");

        Stream flujo = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Task.FromResult(flujo);
    }

    /// <summary>Un temporal nuevo para escribir el zip de esa solicitud. Cada intento usa el suyo.</summary>
    public (FileStream Flujo, string RutaTemporal) CrearTemporal(Guid tenantId, Guid solicitudId)
    {
        var final = RutaComprobada(tenantId, UbicacionPara(tenantId, solicitudId));
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        var temporal = $"{final}{MarcaTemporal}{Guid.NewGuid():N}";
        return (new FileStream(temporal, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true), temporal);
    }

    /// <summary>Renombra el temporal a su ubicación final y devuelve el tamaño.</summary>
    public long Confirmar(Guid tenantId, string ubicacion, string rutaTemporal)
    {
        var final = RutaComprobada(tenantId, ubicacion);
        File.Move(rutaTemporal, final, overwrite: true);
        return new FileInfo(final).Length;
    }

    public static void DescartarTemporal(string? rutaTemporal)
    {
        if (rutaTemporal is not null && File.Exists(rutaTemporal))
            File.Delete(rutaTemporal);
    }

    /// <summary>Borra el zip. No falla si ya no está: describe un estado final.</summary>
    public void Eliminar(Guid tenantId, string ubicacion)
    {
        var ruta = RutaComprobada(tenantId, ubicacion);
        if (File.Exists(ruta))
            File.Delete(ruta);
    }

    /// <summary>
    /// Borra de la carpeta del Tenant los temporales y los zips que ya no son de ninguna solicitud viva o lista
    /// (<paramref name="ubicacionesVigentes"/>) y que nadie toca desde antes de <paramref name="anterioresAUtc"/>:
    /// los que deja un proceso que murió a mitad, un guardado que falló tras renombrar el temporal o un borrado
    /// que no se pudo hacer. Solo mira ficheros de nombre conocido; devuelve cuántos borró.
    /// </summary>
    public int BarrerHuerfanos(Guid tenantId, IReadOnlySet<string> ubicacionesVigentes, DateTime anterioresAUtc)
    {
        var carpetaTenant = Path.Combine(_rutaBase, tenantId.ToString("N"));
        if (!Directory.Exists(carpetaTenant))
            return 0;

        var borrados = 0;
        foreach (var ruta in Directory.EnumerateFiles(carpetaTenant))
        {
            var nombre = Path.GetFileName(ruta);
            var esTemporal = nombre.Contains(".zip" + MarcaTemporal, StringComparison.Ordinal);
            var esZip = nombre.EndsWith(".zip", StringComparison.Ordinal);
            if (!esTemporal && !(esZip && !ubicacionesVigentes.Contains($"{tenantId:N}/{nombre}")))
                continue;
            if (File.GetLastWriteTimeUtc(ruta) >= anterioresAUtc)
                continue;

            try
            {
                File.Delete(ruta);
                borrados++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Uno que no se deja borrar (p. ej. abierto en Windows) no detiene el resto; la próxima vuelta lo reintenta.
            }
        }
        return borrados;
    }

    private string RutaComprobada(Guid tenantId, string ubicacion)
    {
        var carpetaTenant = Path.GetFullPath(Path.Combine(_rutaBase, tenantId.ToString("N"))) + Path.DirectorySeparatorChar;
        var ruta = Path.GetFullPath(Path.Combine(_rutaBase, ubicacion));
        if (!ruta.StartsWith(carpetaTenant, StringComparison.Ordinal) || !ubicacion.StartsWith($"{tenantId:N}/", StringComparison.Ordinal))
            throw new FileNotFoundException("La exportación no está en el almacén.");
        return ruta;
    }
}
