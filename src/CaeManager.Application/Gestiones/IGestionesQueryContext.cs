using CaeManager.Domain.Gestiones;

namespace CaeManager.Application.Gestiones;

public interface IGestionesQueryContext
{
    IQueryable<Gestion> Gestiones { get; }
}
