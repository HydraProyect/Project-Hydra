using CaeManager.Application.Common.Archivos;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// Da por válida cualquier clave de archivo: para los tests de Crear/RenovarDocumento que prueban otra cosa (tipo
/// archivado, acreditaciones) y no necesitan un almacenamiento con contenido. La validación real de la firma la prueban
/// <c>CrearRenovarDocumentoValidanArchivoAlmacenadoTests</c> y <c>VerificadorArchivoAlmacenadoTests</c>.
/// </summary>
public sealed class VerificadorArchivoAlmacenadoPermisivo : IVerificadorArchivoAlmacenado
{
    public Task<bool> EsDocumentoValidoAsync(string archivoUrl, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
}
