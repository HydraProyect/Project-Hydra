using CaeManager.Domain.Subcontratas;

namespace CaeManager.Application.Tests.Subcontratas;

/// <summary>Fake en memoria — los handlers de Application se prueban sin base de datos (ver CODING_STANDARDS.md).</summary>
public class VerificacionExternaSubcontrataRepositorioFalso : IVerificacionExternaSubcontrataRepository
{
    public List<VerificacionExternaSubcontrata> Verificaciones { get; } = [];

    public Task<VerificacionExternaSubcontrata?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Verificaciones.FirstOrDefault(v => v.Id == id));

    public void Agregar(VerificacionExternaSubcontrata verificacion) => Verificaciones.Add(verificacion);
}
