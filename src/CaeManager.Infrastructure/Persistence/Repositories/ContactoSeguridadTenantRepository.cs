using CaeManager.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

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
}
