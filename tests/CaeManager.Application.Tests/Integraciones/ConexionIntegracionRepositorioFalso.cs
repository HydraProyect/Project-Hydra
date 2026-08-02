using CaeManager.Domain.Integraciones;

namespace CaeManager.Application.Tests.Integraciones;

public class ConexionIntegracionRepositorioFalso : IConexionIntegracionRepository
{
    public List<ConexionIntegracion> Conexiones { get; } = [];

    public Task<ConexionIntegracion?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Conexiones.FirstOrDefault(c => c.Id == id));

    public void Agregar(ConexionIntegracion conexion) => Conexiones.Add(conexion);
}
