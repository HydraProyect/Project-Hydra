namespace CaeManager.Domain.Integraciones;

public interface IConexionIntegracionRepository
{
    Task<ConexionIntegracion?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puerta de toda llamada a Graph que lee o envía con la conexión: relee de la base su estado y su verificación
    /// (<see cref="ConexionIntegracion.EsOperativaVerificada"/>), sin fiarse de la instancia cargada antes, que otro
    /// proceso puede haber dejado obsoleta (ConError, desconectada, otra verificación). false si no existe.
    /// </summary>
    Task<bool> SigueOperativaVerificadaAsync(Guid id, DateTime ahoraUtc, CancellationToken cancellationToken = default);

    void Agregar(ConexionIntegracion conexion);
}
