using CaeManager.Domain.Reportes;

namespace CaeManager.Application.Reportes;

public interface IReportesQueryContext
{
    IQueryable<HistorialInforme> HistorialInformes { get; }
}
