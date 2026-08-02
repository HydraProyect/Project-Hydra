namespace CaeManager.Domain.Configuracion;

public interface IFiltroGuardadoRepository
{
    Task<FiltroGuardado?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    void Agregar(FiltroGuardado filtro);

    void Eliminar(FiltroGuardado filtro);
}
