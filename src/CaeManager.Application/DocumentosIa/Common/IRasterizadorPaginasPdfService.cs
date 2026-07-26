using CaeManager.Domain.Common;

namespace CaeManager.Application.DocumentosIa.Common;

/// <summary>
/// Rasteriza páginas específicas de un PDF a imágenes PNG para pasarlas a
/// un proveedor de OCR página a página. Solo necesario en el Caso Mixto
/// del <see cref="DocumentAIRouterService"/>: evita pagar OCR por páginas
/// que ya contienen texto digital embebido.
/// </summary>
public interface IRasterizadorPaginasPdfService
{
    /// <param name="indicesPaginas">Índices 0-based de las páginas a rasterizar.</param>
    Result<IReadOnlyList<byte[]>> RasterizarPaginas(byte[] contenidoPdf, IReadOnlyList<int> indicesPaginas);
}
