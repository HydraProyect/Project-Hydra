namespace CaeManager.Domain.Documentos;

public interface IFirmaGuardadaUsuarioRepository
{
    void Agregar(FirmaGuardadaUsuario firma);

    Task<FirmaGuardadaUsuario?> ObtenerPorUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken = default);
}
