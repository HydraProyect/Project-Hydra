using CaeManager.Domain.Telemetria;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class RegistroTiempoGestionRepository(CaeManagerDbContext dbContext) : IRegistroTiempoGestionRepository
{
    public void Agregar(RegistroTiempoGestion registro) => dbContext.RegistrosTiempoGestion.Add(registro);
}
