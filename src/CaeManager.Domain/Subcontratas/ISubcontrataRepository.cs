namespace CaeManager.Domain.Subcontratas;

public interface ISubcontrataRepository
{
    Task<Subcontrata?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> ExisteConRazonSocialAsync(string razonSocial, Guid? excluirId = null, CancellationToken cancellationToken = default);

    /// <summary>Una Subcontrata con Trabajadores no puede eliminarse (ver EliminarSubcontrataCommand).</summary>
    Task<bool> TieneTrabajadoresAsync(Guid subcontrataId, CancellationToken cancellationToken = default);

    void Agregar(Subcontrata subcontrata);
}
