namespace CaeManager.Domain.Subcontratas;

public interface ISubcontrataClienteRepository
{
    Task<IReadOnlyList<SubcontrataCliente>> ObtenerPorSubcontrataAsync(Guid subcontrataId, CancellationToken cancellationToken = default);

    void Agregar(SubcontrataCliente subcontrataCliente);

    void Eliminar(SubcontrataCliente subcontrataCliente);
}
