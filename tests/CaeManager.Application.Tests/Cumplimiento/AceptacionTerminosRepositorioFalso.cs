using CaeManager.Domain.Cumplimiento;

namespace CaeManager.Application.Tests.Cumplimiento;

public class AceptacionTerminosRepositorioFalso : IAceptacionTerminosRepository
{
    public List<AceptacionTerminos> Aceptaciones { get; } = [];

    public Task<bool> ExisteParaVersionAsync(Guid usuarioId, string versionDocumento, CancellationToken cancellationToken = default) =>
        Task.FromResult(Aceptaciones.Any(a => a.UsuarioId == usuarioId && a.VersionDocumento == versionDocumento));

    public void Agregar(AceptacionTerminos aceptacion) => Aceptaciones.Add(aceptacion);
}
