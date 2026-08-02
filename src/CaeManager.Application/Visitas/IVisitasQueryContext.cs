using CaeManager.Domain.Visitas;

namespace CaeManager.Application.Visitas;

public interface IVisitasQueryContext
{
    IQueryable<Visita> Visitas { get; }
    IQueryable<VisitaTrabajador> VisitasTrabajadores { get; }
}
