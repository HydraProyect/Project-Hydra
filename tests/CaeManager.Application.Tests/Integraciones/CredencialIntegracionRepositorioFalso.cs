using CaeManager.Domain.Integraciones;

namespace CaeManager.Application.Tests.Integraciones;

public class CredencialIntegracionRepositorioFalso : ICredencialIntegracionRepository
{
    public List<CredencialIntegracion> Credenciales { get; } = [];

    public Task<CredencialIntegracion?> ObtenerPorConexionAsync(Guid conexionIntegracionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Credenciales.FirstOrDefault(c => c.ConexionIntegracionId == conexionIntegracionId));

    public void Agregar(CredencialIntegracion credencial) => Credenciales.Add(credencial);
}
