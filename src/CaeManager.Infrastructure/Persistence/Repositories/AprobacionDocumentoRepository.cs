using CaeManager.Domain.Documentos;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class AprobacionDocumentoRepository(CaeManagerDbContext dbContext) : IAprobacionDocumentoRepository
{
    public void Agregar(AprobacionDocumento aprobacion) => dbContext.AprobacionesDocumento.Add(aprobacion);
}
