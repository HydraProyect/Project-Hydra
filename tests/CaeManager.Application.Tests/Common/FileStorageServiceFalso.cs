using CaeManager.Application.Common;

namespace CaeManager.Application.Tests.Common;

/// <summary>Fake en memoria — nunca toca disco ni red real (ver Project-Hydra-Negocio/tecnico/CODING_STANDARDS.md).</summary>
public class FileStorageServiceFalso : IFileStorageService
{
    private readonly Dictionary<string, byte[]> _archivos = [];
    private int _contador;

    /// <summary>Cuántos archivos se guardaron — para probar que un fallo posterior no deja un blob huérfano.</summary>
    public int ArchivosGuardados => _archivos.Count;

    public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default)
    {
        using var memoria = new MemoryStream();
        contenido.CopyTo(memoria);

        var identificador = $"falso-{++_contador}-{nombreArchivoOriginal}";
        _archivos[identificador] = memoria.ToArray();
        return Task.FromResult(identificador);
    }

    /// <summary>Si se informa, <see cref="AbrirAsync"/> la lanza: para probar un almacenamiento sin hueco en el presupuesto de memoria.</summary>
    public Exception? ExcepcionAlAbrir { get; set; }

    public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default)
    {
        if (ExcepcionAlAbrir is not null)
            throw ExcepcionAlAbrir;

        return Task.FromResult<Stream>(new MemoryStream(_archivos[identificador]));
    }

    /// <summary>Si se informa, <see cref="EliminarAsync"/> la lanza sin borrar: para probar la limpieza best-effort que solo avisa.</summary>
    public Exception? ExcepcionAlEliminar { get; set; }

    public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default)
    {
        if (ExcepcionAlEliminar is not null)
            throw ExcepcionAlEliminar;

        _archivos.Remove(identificador);
        return Task.CompletedTask;
    }
}
