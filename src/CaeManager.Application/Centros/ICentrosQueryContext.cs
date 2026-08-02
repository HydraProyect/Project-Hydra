using CaeManager.Domain.Centros;

namespace CaeManager.Application.Centros;

public interface ICentrosQueryContext
{
    IQueryable<Centro> Centros { get; }
    IQueryable<CanalGestionDocumental> CanalesGestionDocumental { get; }
}
