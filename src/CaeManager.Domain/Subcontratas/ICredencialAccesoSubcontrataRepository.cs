namespace CaeManager.Domain.Subcontratas;

public interface ICredencialAccesoSubcontrataRepository
{
    Task<CredencialAccesoSubcontrata?> ObtenerPorSubcontrataAsync(Guid subcontrataId, CancellationToken cancellationToken = default);

    void Agregar(CredencialAccesoSubcontrata credencial);
}
