using CaeManager.Domain.ContratosAcceso;

namespace CaeManager.Application.ContratosAcceso;

public interface IContratosAccesoQueryContext
{
    IQueryable<ContratoAcceso> ContratosAcceso { get; }
}
