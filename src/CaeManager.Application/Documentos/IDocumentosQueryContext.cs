using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Documentos;

public interface IDocumentosQueryContext
{
    IQueryable<Documento> Documentos { get; }
    IQueryable<RevisionIaDocumento> RevisionesIaDocumento { get; }
    IQueryable<AprobacionDocumento> AprobacionesDocumento { get; }
}
