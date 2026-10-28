using CaeManager.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ContactoSeguridadTenantRepository(CaeManagerDbContext dbContext) : IContactoSeguridadTenantRepository
{
    public async Task<IReadOnlyList<ContactoSeguridadTenant>> ObtenerPendienteYVigenteAsync(
        CancellationToken cancellationToken) =>
        await dbContext.ContactosSeguridadTenant
            .Where(c => c.Estado == EstadoContactoSeguridad.Propuesto || c.Estado == EstadoContactoSeguridad.Validado)
            .ToListAsync(cancellationToken);

    public Task<ContactoSeguridadTenant?> ObtenerUltimoRechazadoAsync(CancellationToken cancellationToken) =>
        dbContext.ContactosSeguridadTenant
            .AsNoTracking()
            .Where(c => c.Estado == EstadoContactoSeguridad.Rechazado)
            .OrderByDescending(c => c.ResueltoEnUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public void Agregar(ContactoSeguridadTenant contacto) => dbContext.ContactosSeguridadTenant.Add(contacto);

    public async Task<bool> GuardarPropuestaAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        // Solo el índice único parcial de una Propuesta por Tenant, no cualquier 23505: otra violación de
        // unicidad sería un error real y tiene que propagarse (mismo criterio que
        // OrdenMenuLateralRepository.AgregarYGuardarAsync).
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "IX_ContactosSeguridadTenant_UnoPropuestoYUnoValidadoPorTenant"
        })
        {
            // Lo que este guardado añadió o tocó ya no es cierto; solo se sueltan los contactos de seguridad,
            // no el resto del rastreo del circuito (mismo criterio que ContactosSeguridadPlataforma.ResolverAsync).
            foreach (var entrada in dbContext.ChangeTracker.Entries<ContactoSeguridadTenant>().ToList())
                entrada.State = EntityState.Detached;
            return false;
        }
    }
}
