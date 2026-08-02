using CaeManager.Domain.ApiKeys;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ClaveApiRepository(CaeManagerDbContext dbContext) : IClaveApiRepository
{
    public Task<ClaveApi?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.ClavesApi.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    // IgnoreQueryFilters justificado (ver IClaveApiRepository): al autenticar
    // una petición de la API pública todavía no hay tenant resuelto — es
    // exactamente lo que esta consulta va a determinar. TenantSelladoInterceptor
    // sigue protegiendo cualquier escritura posterior sobre la fila.
    public Task<ClaveApi?> ObtenerPorHashAsync(string hashClave, CancellationToken cancellationToken = default) =>
        dbContext.ClavesApi.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.HashClave == hashClave && !c.EstaEliminado, cancellationToken);

    public void Agregar(ClaveApi clave) => dbContext.ClavesApi.Add(clave);
}
