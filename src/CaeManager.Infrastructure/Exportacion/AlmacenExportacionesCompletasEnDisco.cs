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
}

/// <summary>
/// Los zips de exportación completa en disco, una carpeta por Tenant propietario. Se escriben en un temporal y se
/// renombran al terminar, así que la ruta final nunca existe a medias. Los ficheros ya van cifrados con la
/// contraseña de la exportación (AES-256 del zip); no se cifran otra vez. La ubicación que guarda la fila es
/// relativa (<c>TenantId/SolicitudId.zip</c>) y se comprueba al abrir: una ubicación de otro Tenant, o que salga
/// de su carpeta, no se abre.
/// </summary>
public class AlmacenExportacionesCompletasEnDisco : IAlmacenExportacionesCompletas
{
    private readonly string _rutaBase;

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
        var temporal = $"{final}.tmp-{Guid.NewGuid():N}";
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

    private string RutaComprobada(Guid tenantId, string ubicacion)
    {
        var carpetaTenant = Path.GetFullPath(Path.Combine(_rutaBase, tenantId.ToString("N"))) + Path.DirectorySeparatorChar;
        var ruta = Path.GetFullPath(Path.Combine(_rutaBase, ubicacion));
        if (!ruta.StartsWith(carpetaTenant, StringComparison.Ordinal) || !ubicacion.StartsWith($"{tenantId:N}/", StringComparison.Ordinal))
            throw new FileNotFoundException("La exportación no está en el almacén.");
        return ruta;
    }
}
