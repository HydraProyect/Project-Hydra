using CaeManager.Application.Common;

namespace CaeManager.Application.Tests.Common;

/// <summary>Fake en memoria — nunca toca disco ni red real (ver CODING_STANDARDS.md).</summary>
public class FileStorageServiceFalso : IFileStorageService
{
    private readonly Dictionary<string, byte[]> _archivos = [];
    private int _contador;

    public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default)
    {
        using var memoria = new MemoryStream();
        contenido.CopyTo(memoria);

        var identificador = $"falso-{++_contador}-{nombreArchivoOriginal}";
        _archivos[identificador] = memoria.ToArray();
        return Task.FromResult(identificador);
    }

    public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(new MemoryStream(_archivos[identificador]));

    public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default)
    {
        _archivos.Remove(identificador);
        return Task.CompletedTask;
    }
}
