using CaeManager.Domain.Comunicaciones;

namespace CaeManager.Application.Tests.Comunicaciones;

public class SugerenciaVisitaCorreoRepositorioFalso : ISugerenciaVisitaCorreoRepository
{
    public List<SugerenciaVisitaCorreo> Sugerencias { get; } = [];

    public Task<SugerenciaVisitaCorreo?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Sugerencias.FirstOrDefault(s => s.Id == id));

    public void Agregar(SugerenciaVisitaCorreo sugerencia) => Sugerencias.Add(sugerencia);
}
