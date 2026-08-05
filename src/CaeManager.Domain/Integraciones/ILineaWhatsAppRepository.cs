namespace CaeManager.Domain.Integraciones;

public interface ILineaWhatsAppRepository
{
    /// <summary>Incluye MiembrosPool — lo necesitan el enrutamiento de la ingesta y la pantalla de edición.</summary>
    Task<LineaWhatsApp?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Incluye MiembrosPool. Para la ingesta y el envío saliente, que parten de la conexión de la conversación.</summary>
    Task<LineaWhatsApp?> ObtenerPorConexionAsync(Guid conexionIntegracionId, CancellationToken cancellationToken = default);

    void Agregar(LineaWhatsApp linea);
}
