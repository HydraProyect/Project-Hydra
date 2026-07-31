using CaeManager.Application.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.FileStorage;

/// <summary>
/// Almacenamiento sobre disco local, particionado por tenant (ver
/// docs/MULTITENANCY.md § 4.6 y PLAN-MIGRACION-MULTITENANT.md § 5, Etapa 4):
/// todo archivo nuevo se guarda bajo <c>{tenantId}/{archivo}</c>, nunca en la
/// carpeta plana. Registrado como Scoped (antes Singleton) precisamente para
/// poder depender de <see cref="ITenantActual"/>, que es scoped por
/// naturaleza — inyectar un servicio scoped en un singleton sería una
/// dependencia cautiva (capturaría el primer tenant resuelto para siempre).
/// </summary>
public class DiskFileStorageService : IFileStorageService
{
    private readonly string _rutaBase;
    private readonly ITenantActual _tenantActual;

    public DiskFileStorageService(IOptions<DiskFileStorageServiceOptions> opciones, IHostEnvironment entorno, ITenantActual tenantActual)
    {
        _rutaBase = Path.IsPathRooted(opciones.Value.Ruta)
            ? opciones.Value.Ruta
            : Path.Combine(entorno.ContentRootPath, opciones.Value.Ruta);
        _tenantActual = tenantActual;

        Directory.CreateDirectory(_rutaBase);
    }

    public async Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantActual.TenantId
            ?? throw new InvalidOperationException("No se puede guardar un archivo sin un tenant resuelto.");

        var carpetaTenant = CarpetaDeTenant(tenantId);
        Directory.CreateDirectory(Path.Combine(_rutaBase, carpetaTenant));

        var extension = Path.GetExtension(nombreArchivoOriginal);
        var nombreArchivo = $"{Guid.NewGuid():N}{extension}";
        var identificador = $"{carpetaTenant}/{nombreArchivo}";
        var rutaCompleta = Path.Combine(_rutaBase, carpetaTenant, nombreArchivo);

        await using var destino = File.Create(rutaCompleta);
        await contenido.CopyToAsync(destino, cancellationToken);

        return identificador;
    }

    public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default)
    {
        var rutaCompleta = ResolverRutaSegura(identificador);

        // Un identificador de otro tenant (o sin tenant resuelto) se
        // comporta exactamente igual que "no existe" — nunca se revela que
        // pertenece a otro tenant, mismo criterio que el fix IDOR del
        // Issue #18 para los Ids de entidades.
        if (rutaCompleta is null || !File.Exists(rutaCompleta))
            throw new FileNotFoundException("No encontramos el archivo solicitado.", identificador);

        Stream flujo = File.OpenRead(rutaCompleta);
        return Task.FromResult(flujo);
    }

    public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default)
    {
        var rutaCompleta = ResolverRutaSegura(identificador);

        // Misma resolución segura que al abrir: un identificador de otro
        // tenant no resuelve, así que la purga de un tenant nunca puede
        // borrar archivos de otro.
        if (rutaCompleta is not null && File.Exists(rutaCompleta))
            File.Delete(rutaCompleta);

        return Task.CompletedTask;
    }

    private string? ResolverRutaSegura(string identificador)
    {
        if (_tenantActual.TenantId is not { } tenantId) return null;

        var partes = identificador.Split('/', 2);
        if (partes.Length != 2) return null;

        // El identificador nunca debe permitir escapar del directorio base
        // (path traversal) — se sanea cada segmento por separado.
        var carpetaSolicitada = Path.GetFileName(partes[0]);
        var nombreArchivo = Path.GetFileName(partes[1]);

        if (!string.Equals(carpetaSolicitada, CarpetaDeTenant(tenantId), StringComparison.Ordinal))
            return null;

        return Path.Combine(_rutaBase, carpetaSolicitada, nombreArchivo);
    }

    private static string CarpetaDeTenant(Guid tenantId) => tenantId.ToString("N");
}
