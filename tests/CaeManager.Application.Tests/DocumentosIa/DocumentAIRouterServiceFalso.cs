using CaeManager.Application.DocumentosIa;
using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Domain.Common;

namespace CaeManager.Application.Tests.DocumentosIa;

public class DocumentAIRouterServiceFalso(Result<ExtraccionEstructuradaDto> resultado) : IDocumentAIRouterService
{
    public Task<Result<ExtraccionEstructuradaDto>> ProcesarAsync(
        byte[] contenido, string nombreArchivo, string tipoEsperado, Guid? documentoId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(resultado);
}
