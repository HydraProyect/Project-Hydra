namespace CaeManager.Domain.Proyectos;

public interface IProyectoRequisitoRepository
{
    Task<IReadOnlyList<ProyectoRequisito>> ListarDeProyectoAsync(Guid proyectoId, CancellationToken cancellationToken = default);

    void Agregar(ProyectoRequisito requisito);

    void Eliminar(ProyectoRequisito requisito);
}

public interface IPlantillaRequisitosEventoRepository
{
    Task<PlantillaRequisitosEvento?> ObtenerConTiposAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> ExisteNombreParaClienteAsync(Guid clienteId, string nombre, CancellationToken cancellationToken = default);

    void Agregar(PlantillaRequisitosEvento plantilla);
}
