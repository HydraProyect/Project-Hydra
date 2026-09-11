using CaeManager.Application.Subcontratas;
using CaeManager.Domain.Subcontratas;

namespace CaeManager.Application.Tests.Subcontratas;

public class CredencialAccesoSubcontrataRepositorioFalso : ICredencialAccesoSubcontrataRepository
{
    public List<CredencialAccesoSubcontrata> Credenciales { get; } = [];

    public Task<CredencialAccesoSubcontrata?> ObtenerPorSubcontrataAsync(Guid subcontrataId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Credenciales.FirstOrDefault(c => c.SubcontrataId == subcontrataId));

    public void Agregar(CredencialAccesoSubcontrata credencial) => Credenciales.Add(credencial);
}
