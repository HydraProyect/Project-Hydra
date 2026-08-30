using CaeManager.Domain.Visitas;

namespace CaeManager.Application.Tests.Visitas;

/// <summary>Fake en memoria — los handlers de Application se prueban sin base de datos (ver CODING_STANDARDS.md).</summary>
public class VisitaRepositorioFalso : IVisitaRepository
{
    public List<Visita> Visitas { get; } = [];

    public Task<Visita?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Visitas.FirstOrDefault(v => v.Id == id));

    public void Agregar(Visita visita) => Visitas.Add(visita);
}
