using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.DocumentosIa;

namespace CaeManager.Application.DocumentosIa;

/// <summary>
/// Enruta un documento al proveedor de IA adecuado según su clasificación
/// local (ver docs/ARQUITECTURA-IA-DOCUMENTAL.md § 2.3, los 4 casos), sin
/// que el llamador conozca qué proveedor concreto se usó ni por qué.
/// </summary>
public interface IDocumentAIRouterService
{
    /// <summary>
    /// <paramref name="documentoId"/> es opcional: solo se conoce cuando esta
    /// llamada verifica un Documento ya creado (ver VerificacionIaDocumentoService).
    /// Cuando se indica, la <see cref="AuditoriaExtraccionIa"/> resultante queda
    /// enlazada a ese Documento — el enganche que permite registrar después la
    /// decisión humana (MACRO_PLAN § 6.6).
    /// </summary>
    Task<Result<ExtraccionEstructuradaDto>> ProcesarAsync(
        byte[] contenido, string nombreArchivo, string tipoEsperado, Guid? documentoId = null, CancellationToken cancellationToken = default);
}
