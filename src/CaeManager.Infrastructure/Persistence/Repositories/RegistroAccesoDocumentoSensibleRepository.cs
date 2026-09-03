using CaeManager.Domain.Auditoria;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class RegistroAccesoDocumentoSensibleRepository(CaeManagerDbContext dbContext)
    : IRegistroAccesoDocumentoSensibleRepository
{
    public void Agregar(RegistroAccesoDocumentoSensible registro) =>
        dbContext.RegistrosAccesoDocumentoSensible.Add(registro);
}
