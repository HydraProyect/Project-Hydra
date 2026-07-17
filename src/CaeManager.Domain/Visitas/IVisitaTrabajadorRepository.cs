namespace CaeManager.Domain.Visitas;

public interface IVisitaTrabajadorRepository
{
    Task<IReadOnlyList<VisitaTrabajador>> ObtenerPorVisitaAsync(Guid visitaId, CancellationToken cancellationToken = default);

    void Agregar(VisitaTrabajador visitaTrabajador);

    void Eliminar(VisitaTrabajador visitaTrabajador);
}
