namespace CaeManager.Domain.Clientes;

public interface IClienteRepository
{
    Task<Cliente?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> ExisteConRazonSocialAsync(string razonSocial, Guid? excluirId = null, CancellationToken cancellationToken = default);

    Task<bool> ExisteConCifAsync(string cif, Guid? excluirId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Un Cliente con Centros activos no puede eliminarse: primero hay que reasignar
    /// o dar de baja sus Centros. Ver regla en CrearClienteCommand/EliminarClienteCommand.
    /// </summary>
    Task<bool> TieneCentrosActivosAsync(Guid clienteId, CancellationToken cancellationToken = default);

    void Agregar(Cliente cliente);
}
