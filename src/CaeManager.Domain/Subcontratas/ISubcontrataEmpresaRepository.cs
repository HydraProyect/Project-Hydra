namespace CaeManager.Domain.Subcontratas;

public interface ISubcontrataEmpresaRepository
{
    Task<IReadOnlyList<SubcontrataEmpresa>> ObtenerPorSubcontrataAsync(Guid subcontrataId, CancellationToken cancellationToken = default);

    void Agregar(SubcontrataEmpresa subcontrataEmpresa);

    void Eliminar(SubcontrataEmpresa subcontrataEmpresa);
}
