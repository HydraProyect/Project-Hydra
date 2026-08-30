using CaeManager.Domain.Incidencias;

namespace CaeManager.Application.Tests.Incidencias;

/// <summary>Fake en memoria — los handlers de Application se prueban sin base de datos (ver CODING_STANDARDS.md).</summary>
public class IncidenciaRepositorioFalso : IIncidenciaRepository
{
    public List<Incidencia> Incidencias { get; } = [];

    public Task<Incidencia?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Incidencias.FirstOrDefault(i => i.Id == id));

    public void Agregar(Incidencia incidencia) => Incidencias.Add(incidencia);
}
