namespace CaeManager.Domain.Configuracion;

public interface IEstadoAutomatizacionRepository
{
    Task<EstadoAutomatizacion?> ObtenerPorTrabajoAsync(string trabajoId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EstadoAutomatizacion>> ObtenerTodosAsync(CancellationToken cancellationToken = default);

    void Agregar(EstadoAutomatizacion estado);
}
