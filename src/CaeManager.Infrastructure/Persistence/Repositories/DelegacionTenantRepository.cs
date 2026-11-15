using CaeManager.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class DelegacionTenantRepository(CaeManagerDbContext dbContext) : IDelegacionTenantRepository
{
    public Task<DelegacionTenant?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.DelegacionesTenant.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

    // SQL crudo por necesidad, como DocumentoRepository.BloquearYObtenerIncluidoEliminadoSinRastrearAsync: EF no
    // expresa FOR UPDATE, y la sentencia va sin componer para que el bloqueo quede en la exterior. DelegacionTenant no
    // lleva filtro global de EF que reponer; bajo cae_app_runtime su RLS sigue aplicando, también la política de UPDATE
    // que FOR UPDATE exige. Sin la fila bloqueada no hay garantía que dar: se responde como si no existiera y quien
    // llama falla cerrado.
    //
    // La relectura es la razón de ser del método: una consulta con seguimiento devuelve la instancia que el contexto ya
    // rastrea sin pisar sus valores, así que la delegación leída antes de la transacción seguiría diciendo lo que decía
    // entonces (issue #2232: dos reactivaciones a la vez veían las dos Activa = false y las dos daban éxito).
    public async Task<DelegacionTenant?> BloquearYObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (dbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "El bloqueo de la delegación necesita una transacción abierta: fuera de ella se soltaría en la misma sentencia.");

        var bloqueadas = await dbContext.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM "DelegacionesTenant"
            WHERE "Id" = {id}
            FOR UPDATE
            """).ToListAsync(cancellationToken);
        if (bloqueadas.Count != 1)
            return null;

        var rastreada = dbContext.ChangeTracker.Entries<DelegacionTenant>().FirstOrDefault(e => e.Entity.Id == id);
        if (rastreada is null)
            return await ObtenerPorIdAsync(id, cancellationToken);

        await rastreada.ReloadAsync(cancellationToken);
        return rastreada.State == EntityState.Detached ? null : rastreada.Entity;
    }

    public Task<bool> ExisteActivaAsync(Guid tenantConsultoraId, Guid tenantClienteId, CancellationToken cancellationToken = default) =>
        dbContext.DelegacionesTenant.AnyAsync(
            d => d.TenantConsultoraId == tenantConsultoraId && d.TenantClienteId == tenantClienteId && d.Activa,
            cancellationToken);

    public void Agregar(DelegacionTenant delegacion) => dbContext.DelegacionesTenant.Add(delegacion);
}
