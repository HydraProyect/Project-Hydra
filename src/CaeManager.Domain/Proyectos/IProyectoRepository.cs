namespace CaeManager.Domain.Proyectos;

public interface IProyectoRepository
{
    Task<Proyecto?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> ExisteNombreParaClienteAsync(Guid clienteId, string nombre, Guid? excluirId = null, CancellationToken cancellationToken = default);

    void Agregar(Proyecto proyecto);

    void Eliminar(Proyecto proyecto);
}
