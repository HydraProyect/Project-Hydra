using CaeManager.Domain.Gestiones;

namespace CaeManager.Application.Gestiones;

public interface IGestionesQueryContext
{
    IQueryable<Gestion> Gestiones { get; }
    IQueryable<GestionAutomatica> GestionesAutomaticas { get; }
    IQueryable<PasoGestionAutomatica> PasosGestionAutomatica { get; }
}
