using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Reclamaciones;

namespace CaeManager.Application.Tests.Reclamaciones;

/// <summary>Reclamaciones ya registradas, en memoria; sus líneas salen de las propias reclamaciones.</summary>
public sealed class ReclamacionesQueryContextFalso : IReclamacionesQueryContext
{
    public List<ReclamacionDocumental> Lista { get; } = [];

    public IQueryable<ReclamacionDocumental> ReclamacionesDocumentales =>
        new TestAsyncQueryable<ReclamacionDocumental>(Lista.AsQueryable());

    public IQueryable<ReclamacionDocumentalDocumento> ReclamacionesDocumentalesDocumento =>
        new TestAsyncQueryable<ReclamacionDocumentalDocumento>(Lista.SelectMany(r => r.Documentos).AsQueryable());
}
