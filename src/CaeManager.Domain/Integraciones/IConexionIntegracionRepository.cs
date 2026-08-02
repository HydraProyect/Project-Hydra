namespace CaeManager.Domain.Integraciones;

public interface IConexionIntegracionRepository
{
    Task<ConexionIntegracion?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    void Agregar(ConexionIntegracion conexion);
}
