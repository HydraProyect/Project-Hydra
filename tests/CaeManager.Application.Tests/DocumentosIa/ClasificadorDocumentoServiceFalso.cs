using CaeManager.Application.Common;
using CaeManager.Domain.Common;

namespace CaeManager.Application.Tests.DocumentosIa;

public class ClasificadorDocumentoServiceFalso(Result<ClasificacionDocumentoDto> resultado) : IClasificadorDocumentoService
{
    public Task<Result<ClasificacionDocumentoDto>> ClasificarAsync(
        byte[] contenido, string nombreArchivo, CancellationToken cancellationToken = default) =>
        Task.FromResult(resultado);
}
