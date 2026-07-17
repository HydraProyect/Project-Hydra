using CaeManager.Application.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.FileStorage;

public class DiskFileStorageService : IFileStorageService
{
    private readonly string _rutaBase;

    public DiskFileStorageService(IOptions<DiskFileStorageServiceOptions> opciones, IHostEnvironment entorno)
    {
        _rutaBase = Path.IsPathRooted(opciones.Value.Ruta)
            ? opciones.Value.Ruta
            : Path.Combine(entorno.ContentRootPath, opciones.Value.Ruta);

        Directory.CreateDirectory(_rutaBase);
    }

    public async Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(nombreArchivoOriginal);
        var identificador = $"{Guid.NewGuid():N}{extension}";
        var rutaCompleta = Path.Combine(_rutaBase, identificador);

        await using var destino = File.Create(rutaCompleta);
        await contenido.CopyToAsync(destino, cancellationToken);

        return identificador;
    }

    public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default)
    {
        var rutaCompleta = ResolverRutaSegura(identificador);

        if (!File.Exists(rutaCompleta))
            throw new FileNotFoundException("No encontramos el archivo solicitado.", identificador);

        Stream flujo = File.OpenRead(rutaCompleta);
        return Task.FromResult(flujo);
    }

    private string ResolverRutaSegura(string identificador)
    {
        // El identificador nunca debe permitir escapar del directorio base (path traversal).
        var nombreArchivo = Path.GetFileName(identificador);
        return Path.Combine(_rutaBase, nombreArchivo);
    }
}
