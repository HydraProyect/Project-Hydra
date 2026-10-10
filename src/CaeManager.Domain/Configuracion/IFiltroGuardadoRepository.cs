namespace CaeManager.Domain.Configuracion;

public interface IFiltroGuardadoRepository
{
    Task<FiltroGuardado?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Si el usuario ya tiene, en el Tenant actual y en esa pantalla, un filtro
    /// con ese nombre. El Tenant no es parámetro: lo acota el filtro global.
    /// </summary>
    Task<bool> ExisteConNombreAsync(Guid usuarioId, string pantalla, string nombre, CancellationToken cancellationToken = default);

    /// <summary>
    /// La vista recordada del usuario en esa pantalla, en el Tenant actual (lo
    /// acota el filtro global), o <c>null</c> si no tiene. Devuelve la fila
    /// rastreada y con los valores que hay AHORA en la base: el contexto vive lo
    /// que el circuito, y otra pestaña del mismo usuario pudo cambiarla después de
    /// que este la leyera.
    /// </summary>
    Task<FiltroGuardado?> ObtenerVistaRecordadaAsync(Guid usuarioId, string pantalla, CancellationToken cancellationToken = default);

    void Agregar(FiltroGuardado filtro);

    void Eliminar(FiltroGuardado filtro);
}
