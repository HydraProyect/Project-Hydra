namespace CaeManager.Domain.Integraciones;

public interface ICredencialIntegracionRepository
{
    Task<CredencialIntegracion?> ObtenerPorConexionAsync(Guid conexionIntegracionId, CancellationToken cancellationToken = default);

    void Agregar(CredencialIntegracion credencial);
}
