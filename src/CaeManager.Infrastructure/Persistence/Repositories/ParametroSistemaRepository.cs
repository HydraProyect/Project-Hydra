using CaeManager.Domain.Configuracion;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ParametroSistemaRepository(CaeManagerDbContext dbContext) : IParametroSistemaRepository
{
    public Task<ParametroSistema> ObtenerAsync(CancellationToken cancellationToken = default) =>
        dbContext.ParametrosSistema.SingleAsync(cancellationToken);

    public void Agregar(ParametroSistema parametro) => dbContext.ParametrosSistema.Add(parametro);
}
