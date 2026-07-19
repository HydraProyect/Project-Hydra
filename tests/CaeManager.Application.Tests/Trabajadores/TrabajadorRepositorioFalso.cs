using CaeManager.Domain.Trabajadores;

namespace CaeManager.Application.Tests.Trabajadores;

public class TrabajadorRepositorioFalso : ITrabajadorRepository
{
    public List<Trabajador> Trabajadores { get; } = [];

    public Task<Trabajador?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Trabajadores.FirstOrDefault(t => t.Id == id));

    public Task<bool> ExisteConDniAsync(string dni, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Trabajadores.Any(t => t.Dni == dni.Trim().ToUpperInvariant() && t.Id != excluirId));

    public void Agregar(Trabajador trabajador) => Trabajadores.Add(trabajador);
}
