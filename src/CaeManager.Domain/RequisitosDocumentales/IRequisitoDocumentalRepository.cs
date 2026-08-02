namespace CaeManager.Domain.RequisitosDocumentales;

public interface IRequisitoDocumentalRepository
{
    Task<RequisitoDocumental?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);
    void Agregar(RequisitoDocumental requisito);
    void Eliminar(RequisitoDocumental requisito);
}
