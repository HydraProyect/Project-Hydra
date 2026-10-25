using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Empresas;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class PlataformaCaeClienteEmpresarialRepository(CaeManagerDbContext dbContext) : IPlataformaCaeClienteEmpresarialRepository
{
    // Los dos índices únicos de la caja, con el nombre que EF les dio en la migración 20261018193814
    // (truncado a 63 caracteres). Solo estos: otra violación de unicidad sería un error real y tiene
    // que propagarse (mismo criterio que OrdenMenuLateralRepository).
    private static readonly string[] IndicesUnicos =
    [
        "IX_PlataformasCaeClienteEmpresarial_TenantId_ClienteEmpresaria~",
        "IX_AccesosPlataformaCaeEmpresa_TenantId_ClienteEmpresarialId_E~",
    ];

    public Task<PlataformaCaeClienteEmpresarial?> ObtenerPlataformaAsync(Guid clienteEmpresarialId, CancellationToken cancellationToken = default) =>
        dbContext.ObtenerActualAsync(dbContext.PlataformasCaeClienteEmpresarial
            .Where(p => p.ClienteEmpresarialId == clienteEmpresarialId), cancellationToken);

    public void AgregarPlataforma(PlataformaCaeClienteEmpresarial plataforma) => dbContext.PlataformasCaeClienteEmpresarial.Add(plataforma);

    public Task<AccesoPlataformaCaeEmpresa?> ObtenerAccesoAsync(Guid clienteEmpresarialId, Guid empresaId, CancellationToken cancellationToken = default) =>
        dbContext.AccesosPlataformaCaeEmpresa.FirstOrDefaultAsync(
            a => a.ClienteEmpresarialId == clienteEmpresarialId && a.EmpresaId == empresaId, cancellationToken);

    public void AgregarAcceso(AccesoPlataformaCaeEmpresa acceso) => dbContext.AccesosPlataformaCaeEmpresa.Add(acceso);

    public async Task<bool> GuardarCambiosAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Otra persona cambió la plataforma entre la comprobación de Version y el UPDATE. La edición perdida
            // no se queda Modified (con el token que le dio el interceptor) para que el siguiente SaveChanges del
            // circuito la reintente, ni su auditoría Added. ConcurrenciaBehavior traduce la excepción al conflicto.
            ConcurrenciaContexto.DescartarEdicionPerdida(dbContext, dbContext.ChangeTracker.Entries()
                .Where(e => e.Entity is PlataformaCaeClienteEmpresarial or AccesoPlataformaCaeEmpresa));
            throw;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: { } indice
        } && IndicesUnicos.Contains(indice))
        {
            // El alta que perdió la carrera no se queda Added para el próximo SaveChanges del circuito, ni
            // tampoco los registros que AuditoriaInterceptor añadió para este guardado fallido: un
            // «Creado» de una fila que no existe sería auditoría falsa.
            foreach (var entrada in dbContext.ChangeTracker.Entries()
                         .Where(e => e.State == EntityState.Added
                                     && e.Entity is PlataformaCaeClienteEmpresarial or AccesoPlataformaCaeEmpresa or RegistroAuditoria)
                         .ToList())
                entrada.State = EntityState.Detached;
            return false;
        }
    }
}
