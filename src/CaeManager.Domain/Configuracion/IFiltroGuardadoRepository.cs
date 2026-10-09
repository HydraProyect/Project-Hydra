namespace CaeManager.Domain.Configuracion;

public interface IFiltroGuardadoRepository
{
    Task<FiltroGuardado?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Si el usuario ya tiene, en el Tenant actual y en esa pantalla, un filtro
    /// con ese nombre. El Tenant no es parámetro: lo acota el filtro global.
    /// </summary>
    Task<bool> ExisteConNombreAsync(Guid usuarioId, string pantalla, string nombre, CancellationToken cancellationToken = default);

    void Agregar(FiltroGuardado filtro);

    void Eliminar(FiltroGuardado filtro);
}
