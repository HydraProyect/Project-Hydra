using CaeManager.Domain.Configuracion;

namespace CaeManager.Application.Tests.Configuracion;

public class FiltroGuardadoRepositorioFalso : IFiltroGuardadoRepository
{
    public List<FiltroGuardado> Filtros { get; } = [];

    public Task<FiltroGuardado?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Filtros.FirstOrDefault(f => f.Id == id));

    public void Agregar(FiltroGuardado filtro) => Filtros.Add(filtro);

    public void Eliminar(FiltroGuardado filtro) => Filtros.Remove(filtro);
}
