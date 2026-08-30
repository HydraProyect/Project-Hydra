using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Domain.Common;

namespace CaeManager.Application.Tests.DocumentosIa;

/// <summary>
/// <see cref="VecesLlamado"/> pasa a contar una llamada POR PÁGINA, no una por
/// documento: es lo que permite comprobar que el router pide las páginas de una
/// en una en lugar de reclamarlas todas de golpe.
/// <see cref="UltimosIndicesRasterizados"/> las acumula en orden para que los
/// tests sigan pudiendo afirmar QUÉ páginas se rasterizaron, que es la
/// propiedad que ya vigilaban.
/// </summary>
public class RasterizadorPaginasFalso(bool fallido = false) : IRasterizadorPaginasPdfService
{
    private readonly List<int> _indices = [];

    public int VecesLlamado { get; private set; }
    public IReadOnlyList<int> UltimosIndicesRasterizados => _indices;

    public Result<byte[]> RasterizarPagina(byte[] contenidoPdf, int indicePagina, CancellationToken cancellationToken = default)
    {
        VecesLlamado++;
        _indices.Add(indicePagina);

        if (fallido)
            return Result.Fallo<byte[]>(Error.Crear("Rasterizador.FalloConversion", "error de prueba"));

        return Result.Exito(new byte[] { 0x89, 0x50 });
    }
}
