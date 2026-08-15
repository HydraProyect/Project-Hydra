using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Tests.Documentos;

public class FirmaGuardadaUsuarioRepositorioFalso : IFirmaGuardadaUsuarioRepository
{
    public List<FirmaGuardadaUsuario> Firmas { get; } = [];

    public void Agregar(FirmaGuardadaUsuario firma) => Firmas.Add(firma);

    public Task<FirmaGuardadaUsuario?> ObtenerPorUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Firmas.FirstOrDefault(f => f.UsuarioId == usuarioId));
}
