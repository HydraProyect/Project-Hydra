using CaeManager.Domain.DocumentosIa;

namespace CaeManager.Application.Tests.DocumentosIa;

public class AuditoriaExtraccionIaRepositorioFalso : IAuditoriaExtraccionIaRepository
{
    public List<AuditoriaExtraccionIa> Auditorias { get; } = [];

    public void Agregar(AuditoriaExtraccionIa auditoria) => Auditorias.Add(auditoria);
}
