namespace CaeManager.Domain.Cumplimiento;

public interface IAceptacionTerminosRepository
{
    Task<bool> ExisteParaVersionAsync(Guid usuarioId, string versionDocumento, CancellationToken cancellationToken = default);

    void Agregar(AceptacionTerminos aceptacion);
}
