using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Domain.Common;

namespace CaeManager.Application.DocumentosIa;

/// <summary>
/// Enruta un documento al proveedor de IA adecuado según su clasificación
/// local (ver docs/ARQUITECTURA-IA-DOCUMENTAL.md § 2.3, los 4 casos), sin
/// que el llamador conozca qué proveedor concreto se usó ni por qué.
/// </summary>
public interface IDocumentAIRouterService
{
    Task<Result<ExtraccionEstructuradaDto>> ProcesarAsync(
        byte[] contenido, string nombreArchivo, string tipoEsperado, CancellationToken cancellationToken = default);
}
