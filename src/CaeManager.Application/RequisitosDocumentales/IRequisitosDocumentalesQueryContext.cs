using CaeManager.Domain.RequisitosDocumentales;

namespace CaeManager.Application.RequisitosDocumentales;

public interface IRequisitosDocumentalesQueryContext
{
    IQueryable<RequisitoDocumental> RequisitosDocumentales { get; }
}
