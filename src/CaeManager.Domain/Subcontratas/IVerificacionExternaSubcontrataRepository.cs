namespace CaeManager.Domain.Subcontratas;

public interface IVerificacionExternaSubcontrataRepository
{
    Task<VerificacionExternaSubcontrata?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    void Agregar(VerificacionExternaSubcontrata verificacion);
}
