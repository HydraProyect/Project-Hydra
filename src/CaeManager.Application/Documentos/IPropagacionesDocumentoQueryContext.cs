using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Documentos;

public interface IPropagacionesDocumentoQueryContext
{
    IQueryable<PropagacionDocumento> Propagaciones { get; }
}
