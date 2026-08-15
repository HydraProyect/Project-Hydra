using CaeManager.Domain.DocumentosIa;

namespace CaeManager.Application.Tests.DocumentosIa;

public class AuditoriaExtraccionIaRepositorioFalso : IAuditoriaExtraccionIaRepository
{
    public List<AuditoriaExtraccionIa> Auditorias { get; } = [];

    public void Agregar(AuditoriaExtraccionIa auditoria) => Auditorias.Add(auditoria);

    public Task<AuditoriaExtraccionIa?> ObtenerUltimaSinDecisionPorDocumentoAsync(Guid documentoId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Auditorias
            .Where(a => a.DocumentoId == documentoId && a.DecisionHumana is null)
            .OrderByDescending(a => a.CreadaEnUtc)
            .FirstOrDefault());
}
