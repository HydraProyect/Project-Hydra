namespace CaeManager.Domain.VigilanciaNormativa;

public interface IAvisoRevisionNormativaRepository
{
    Task<bool> ExisteParaPublicacionAsync(string identificadorBoe, CancellationToken cancellationToken = default);

    void Agregar(AvisoRevisionNormativa aviso);
}
