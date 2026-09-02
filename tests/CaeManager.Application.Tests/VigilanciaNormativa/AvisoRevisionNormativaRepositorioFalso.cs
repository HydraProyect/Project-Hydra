using CaeManager.Domain.VigilanciaNormativa;

namespace CaeManager.Application.Tests.VigilanciaNormativa;

public class AvisoRevisionNormativaRepositorioFalso : IAvisoRevisionNormativaRepository
{
    public List<AvisoRevisionNormativa> Avisos { get; } = [];

    public Task<bool> ExisteParaPublicacionAsync(string identificadorBoe, CancellationToken cancellationToken = default) =>
        Task.FromResult(Avisos.Any(a => a.IdentificadorBoe == identificadorBoe));

    public Task<AvisoRevisionNormativa?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Avisos.FirstOrDefault(a => a.Id == id));

    public void Agregar(AvisoRevisionNormativa aviso) => Avisos.Add(aviso);
}
