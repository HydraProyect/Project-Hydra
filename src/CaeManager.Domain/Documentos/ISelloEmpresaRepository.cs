namespace CaeManager.Domain.Documentos;

public interface ISelloEmpresaRepository
{
    void Agregar(SelloEmpresa sello);

    Task<SelloEmpresa?> ObtenerPorEmpresaAsync(Guid empresaId, CancellationToken cancellationToken = default);
}
