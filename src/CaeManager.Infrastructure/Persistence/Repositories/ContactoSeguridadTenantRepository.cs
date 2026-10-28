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
            // La base rechazó el lote entero: ni la propuesta perdedora, ni la sustitución de la pendiente, ni la
            // auditoría que añadió el interceptor pueden reintentarse en un guardado posterior del circuito
            // (mismo criterio que el choque de CIF en CaeManagerDbContext).
            ConcurrenciaContexto.DescartarLoteFallido(dbContext);
            return false;
        }
        // La otra carrera: las dos propuestas pasaron a Sustituida la misma pendiente y la perdedora actualizó 0
        // filas. CaeManagerDbContext ya descartó el lote; solo se traduce si el choque es de contactos de
        // seguridad, que es lo único que este guardado escribe además de su auditoría.
        catch (DbUpdateConcurrencyException ex) when (ex.Entries.Count > 0
            && ex.Entries.All(e => e.Entity is ContactoSeguridadTenant))
        {
            return false;
        }
    }
}
