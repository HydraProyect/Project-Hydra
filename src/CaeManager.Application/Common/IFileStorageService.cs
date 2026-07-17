namespace CaeManager.Application.Common;

/// <summary>
/// Almacenamiento de archivos adjuntos (PDFs de Documento). La implementación
/// real vive en Infrastructure — hoy sobre disco local, preparada para
/// cambiar a almacenamiento en la nube sin tocar Application ni Presentation
/// (ver ARCHITECTURE.md, "Archivos").
/// </summary>
public interface IFileStorageService
{
    /// <summary>Guarda el contenido y devuelve un identificador opaco para recuperarlo después.</summary>
    Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default);

    Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default);
}
