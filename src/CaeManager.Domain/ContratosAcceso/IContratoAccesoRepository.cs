namespace CaeManager.Domain.ContratosAcceso;

public interface IContratoAccesoRepository
{
    Task<ContratoAcceso?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    void Agregar(ContratoAcceso contrato);
}
