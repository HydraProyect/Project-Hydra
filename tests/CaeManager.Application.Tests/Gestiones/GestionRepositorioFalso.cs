using CaeManager.Domain.Gestiones;

namespace CaeManager.Application.Tests.Gestiones;

/// <summary>Fake en memoria — los handlers de Application se prueban sin base de datos (ver CODING_STANDARDS.md).</summary>
public class GestionRepositorioFalso : IGestionRepository
{
    public List<Gestion> Gestiones { get; } = [];

    public Task<Gestion?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Gestiones.FirstOrDefault(g => g.Id == id));

    public void Agregar(Gestion gestion) => Gestiones.Add(gestion);
}
