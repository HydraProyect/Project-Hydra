using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Empresas;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class CatalogoTrabajadoresEmpresaRepository(CaeManagerDbContext dbContext) : ICatalogoTrabajadoresEmpresaRepository
{
    public Task<DelegacionEmpresa?> ObtenerDelegacionEmpresaAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.ObtenerActualAsync(dbContext.DelegacionesEmpresa.Where(d => d.Id == id), cancellationToken);

    public Task<OficioEmpresa?> ObtenerOficioAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.ObtenerActualAsync(dbContext.OficiosEmpresa.Where(o => o.Id == id), cancellationToken);

    public async Task<IReadOnlyList<DelegacionEmpresa>> ListarDelegacionesEmpresaAsync(Guid empresaId, CancellationToken cancellationToken = default) =>
        await dbContext.DelegacionesEmpresa.Where(d => d.EmpresaId == empresaId).OrderBy(d => d.Nombre).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<OficioEmpresa>> ListarOficiosAsync(Guid empresaId, CancellationToken cancellationToken = default) =>
        await dbContext.OficiosEmpresa.Where(o => o.EmpresaId == empresaId).OrderBy(o => o.Nombre).ToListAsync(cancellationToken);

    public Task<int> ContarTrabajadoresDeDelegacionEmpresaAsync(Guid delegacionId, CancellationToken cancellationToken = default) =>
        dbContext.Trabajadores.CountAsync(t => t.DelegacionEmpresaId == delegacionId, cancellationToken);

    public Task<int> ContarTrabajadoresDeOficioAsync(Guid oficioId, CancellationToken cancellationToken = default) =>
        dbContext.Trabajadores.CountAsync(t => t.OficioEmpresaId == oficioId, cancellationToken);

    public void Agregar(DelegacionEmpresa delegacion) => dbContext.DelegacionesEmpresa.Add(delegacion);

    public void Agregar(OficioEmpresa oficio) => dbContext.OficiosEmpresa.Add(oficio);

    // Los dos índices únicos de nombre por Empresa que crea la migración AnadeDelegacionYOficioEmpresa. Solo estos: otra
    // violación de unicidad sería un error real y tiene que propagarse (mismo criterio que
    // PlataformaCaeClienteEmpresarialRepository).
    private static readonly string[] IndicesUnicos =
    [
        "UX_DelegacionesEmpresa_TenantId_EmpresaId_Clave",
        "UX_OficiosEmpresa_TenantId_EmpresaId_Clave",
    ];

    public async Task<bool> GuardarCambiosAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: { } indice
        } && IndicesUnicos.Contains(indice))
        {
            // El alta o el renombre que perdió la carrera no se queda pendiente para el próximo SaveChanges del
            // circuito, ni los registros que AuditoriaInterceptor añadió para este guardado fallido.
            foreach (var entrada in dbContext.ChangeTracker.Entries()
                         .Where(e => e.Entity is DelegacionEmpresa or OficioEmpresa or RegistroAuditoria
                                     && e.State is EntityState.Added or EntityState.Modified)
                         .ToList())
            {
                if (entrada.State == EntityState.Added)
                {
                    entrada.State = EntityState.Detached;
                    continue;
                }

                entrada.CurrentValues.SetValues(entrada.OriginalValues);
                entrada.State = EntityState.Unchanged;
            }

            return false;
        }
    }
}
