using CaeManager.Domain.Integraciones;

namespace CaeManager.Application.Tests.Integraciones;

public class CredencialIntegracionRepositorioFalso : ICredencialIntegracionRepository
{
    public List<CredencialIntegracion> Credenciales { get; } = [];

    /// <summary>Si es true, una conexión sin credencial registrada recibe una nueva (refresh token de prueba).</summary>
    public bool CredencialParaCualquierConexion { get; init; }

    public Task<CredencialIntegracion?> ObtenerPorConexionAsync(Guid conexionIntegracionId, CancellationToken cancellationToken = default)
    {
        var credencial = Credenciales.FirstOrDefault(c => c.ConexionIntegracionId == conexionIntegracionId);
        if (credencial is null && CredencialParaCualquierConexion)
        {
            credencial = new CredencialIntegracion(conexionIntegracionId, "refresh-token");
            Credenciales.Add(credencial);
        }

        return Task.FromResult(credencial);
    }

    public void Agregar(CredencialIntegracion credencial) => Credenciales.Add(credencial);
}
