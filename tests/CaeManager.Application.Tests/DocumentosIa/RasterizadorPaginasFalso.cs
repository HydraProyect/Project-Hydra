using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Domain.Common;

namespace CaeManager.Application.Tests.DocumentosIa;

public class RasterizadorPaginasFalso(bool fallido = false) : IRasterizadorPaginasPdfService
{
    public int VecesLlamado { get; private set; }
    public IReadOnlyList<int>? UltimosIndicesRasterizados { get; private set; }

    public Result<IReadOnlyList<byte[]>> RasterizarPaginas(byte[] contenidoPdf, IReadOnlyList<int> indicesPaginas)
    {
        VecesLlamado++;
        UltimosIndicesRasterizados = indicesPaginas;

        if (fallido)
            return Result.Fallo<IReadOnlyList<byte[]>>(Error.Crear("Rasterizador.FalloConversion", "error de prueba"));

        return Result.Exito<IReadOnlyList<byte[]>>(
            indicesPaginas.Select(_ => new byte[] { 0x89, 0x50 }).ToList());
    }
}
