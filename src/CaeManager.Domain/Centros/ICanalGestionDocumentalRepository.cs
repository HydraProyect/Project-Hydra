namespace CaeManager.Domain.Centros;

public interface ICanalGestionDocumentalRepository
{
    Task<CanalGestionDocumental?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Todos los canales de un Centro — los Commands necesitan verlos para resolver el principal.</summary>
    Task<IReadOnlyList<CanalGestionDocumental>> ObtenerPorCentroAsync(Guid centroId, CancellationToken cancellationToken = default);

    void Agregar(CanalGestionDocumental canal);
}
