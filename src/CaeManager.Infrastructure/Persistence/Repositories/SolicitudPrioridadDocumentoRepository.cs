using CaeManager.Domain.Comunicaciones;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class SolicitudPrioridadDocumentoRepository(CaeManagerDbContext dbContext) : ISolicitudPrioridadDocumentoRepository
{
    public void Agregar(SolicitudPrioridadDocumento solicitud) => dbContext.SolicitudesPrioridadDocumento.Add(solicitud);
}
