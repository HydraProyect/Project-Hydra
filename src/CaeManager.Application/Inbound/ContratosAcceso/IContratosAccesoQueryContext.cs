using CaeManager.Domain.Inbound.ContratosAcceso;

namespace CaeManager.Application.Inbound.ContratosAcceso;

public interface IContratosAccesoQueryContext
{
    IQueryable<ContratoAcceso> ContratosAcceso { get; }
}
