using CaeManager.Application.Common;
using CaeManager.Application.Common.Archivos;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Da por válida cualquier clave de archivo: para los tests que construyen a mano el handler de Crear/RenovarDocumento
/// y prueban otra cosa (acreditaciones, concurrencia, ids ajenos) con claves ficticias. La validación real de la firma
/// la prueban los tests unitarios de <see cref="VerificadorArchivoAlmacenado"/> y de los dos handlers.
/// </summary>
public sealed class VerificadorArchivoAlmacenadoPermisivo : IVerificadorArchivoAlmacenado
{
    public Task<bool> EsDocumentoValidoAsync(string archivoUrl, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
}

/// <summary>
/// Almacenamiento que devuelve un PDF mínimo válido para cualquier clave: para los tests que componen el contenedor real
/// de <c>AddApplication()</c> y mandan una clave ficticia a Crear/RenovarDocumento, que ahora comprueba la firma del contenido.
/// </summary>
public sealed class AlmacenamientoConPdfMinimoFalso : IFileStorageService
{
    public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
        Task.FromResult(nombreArchivoOriginal);

    public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(new MemoryStream("%PDF-1.4\n%%EOF"u8.ToArray()));

    public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
