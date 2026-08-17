using CaeManager.Domain.Importacion;

namespace CaeManager.Application.Importacion;

public interface IImportacionQueryContext
{
    IQueryable<HistorialImportacion> HistorialImportaciones { get; }
}
