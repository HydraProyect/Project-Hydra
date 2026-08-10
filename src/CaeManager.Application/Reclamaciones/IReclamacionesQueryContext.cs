using CaeManager.Domain.Reclamaciones;

namespace CaeManager.Application.Reclamaciones;

public interface IReclamacionesQueryContext
{
    IQueryable<ReclamacionDocumental> ReclamacionesDocumentales { get; }
    IQueryable<ReclamacionDocumentalDocumento> ReclamacionesDocumentalesDocumento { get; }
}
