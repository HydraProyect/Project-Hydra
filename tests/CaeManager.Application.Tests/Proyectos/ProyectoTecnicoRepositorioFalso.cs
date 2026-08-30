using CaeManager.Domain.Proyectos;

namespace CaeManager.Application.Tests.Proyectos;

public class ProyectoTecnicoRepositorioFalso : IProyectoTecnicoRepository
{
    public List<ProyectoTecnico> ProyectosTecnicos { get; } = [];

    public Task<ProyectoTecnico?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(ProyectosTecnicos.FirstOrDefault(pt => pt.Id == id));

    public Task<bool> ExisteActivoAsync(Guid proyectoId, Guid trabajadorId, CancellationToken cancellationToken = default) =>
        Task.FromResult(ProyectosTecnicos.Any(pt =>
            pt.ProyectoId == proyectoId && pt.TrabajadorId == trabajadorId && pt.EstaActivo));

    public void Agregar(ProyectoTecnico proyectoTecnico) => ProyectosTecnicos.Add(proyectoTecnico);
}
