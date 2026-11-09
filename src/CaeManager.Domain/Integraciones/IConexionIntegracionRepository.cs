namespace CaeManager.Domain.Integraciones;

public interface IConexionIntegracionRepository
{
    Task<ConexionIntegracion?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Como <see cref="ObtenerPorIdAsync"/>, pero con los valores de la base aunque el contexto (uno por circuito en
    /// Blazor Server) ya tuviera rastreada la instancia: la relee, y su versión pasa a ser la leída. Es la lectura de quien
    /// decide dentro de <c>ICerrojoVerificacionBuzon</c> (Desconectar, Reactivar y la verificación de identidad): la
    /// instancia cargada antes de tomar el cerrojo puede estar obsoleta, y EF no comprueba la versión de una entidad que
    /// no cambia. null si la conexión ya no existe.
    /// </summary>
    Task<ConexionIntegracion?> ObtenerActualPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puerta de toda llamada a Graph que lee o envía con la conexión: relee de la base su estado y su verificación
    /// (<see cref="ConexionIntegracion.EsOperativaVerificada"/>), sin fiarse de la instancia cargada antes, que otro
    /// proceso puede haber dejado obsoleta (ConError, desconectada, otra verificación). false si no existe.
    /// </summary>
    Task<bool> SigueOperativaVerificadaAsync(Guid id, DateTime ahoraUtc, CancellationToken cancellationToken = default);

    void Agregar(ConexionIntegracion conexion);
}
