namespace CaeManager.Domain.Configuracion;

public interface IPreferenciaDashboardUsuarioRepository
{
    Task<PreferenciaDashboardUsuario?> ObtenerPorUsuarioIdAsync(Guid usuarioId, CancellationToken cancellationToken = default);

    void Agregar(PreferenciaDashboardUsuario preferencia);
}
