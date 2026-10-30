using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Integraciones;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ConexionPlataformaCaeRepository(CaeManagerDbContext dbContext) : IConexionPlataformaCaeRepository
{
    private static readonly string[] IndicesUnicos =
    [
        "IX_ConexionesPlataformaCae_TenantId_Nombre",
        "IX_CredencialesConexionPlataformaCae_TenantId_ConexionPlatafor~",
    ];

    public Task<ConexionPlataformaCae?> ObtenerAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.ConexionesPlataformaCae.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);

    public Task<ConexionPlataformaCae?> ObtenerPorNombreAsync(string nombre, CancellationToken cancellationToken = default) =>
        dbContext.ConexionesPlataformaCae.SingleOrDefaultAsync(c => c.Nombre == nombre, cancellationToken);

    public Task<CredencialConexionPlataformaCae?> ObtenerCredencialAsync(Guid conexionId, CancellationToken cancellationToken = default) =>
        dbContext.CredencialesConexionPlataformaCae.SingleOrDefaultAsync(c => c.ConexionPlataformaCaeId == conexionId, cancellationToken);

    public Task<CredencialParaConector?> LeerCredencialAsync(Guid conexionId, CancellationToken cancellationToken = default) =>
        dbContext.CredencialesConexionPlataformaCae
            .Where(c => c.ConexionPlataformaCaeId == conexionId)
            .Select(c => new CredencialParaConector(c.TenantId, c.SecretoApi))
            .SingleOrDefaultAsync(cancellationToken);

    // Proyecta: el canal lleva la contraseña del portal, y esto solo necesita la plataforma. Un documento
    // sustituido no se sube ni se consulta: su Acreditación se trata como no encontrada.
    public Task<AcreditacionParaConector?> ObtenerAcreditacionAsync(Guid acreditacionId, CancellationToken cancellationToken = default) =>
        (from a in dbContext.AcreditacionesDocumentoPlataforma
         where a.Id == acreditacionId
         join c in dbContext.CanalesGestionDocumental on a.CanalGestionDocumentalId equals c.Id
         join d in dbContext.Documentos.Operativos() on a.DocumentoId equals d.Id
         select new AcreditacionParaConector(a.Id, a.TenantId, a.DocumentoId, c.ProveedorPlataformaCaeId, d.ArchivoUrl))
        .SingleOrDefaultAsync(cancellationToken);

    public Task<TrabajadorParaConector?> ObtenerTrabajadorAsync(Guid trabajadorId, CancellationToken cancellationToken = default) =>
        dbContext.Trabajadores
            .Where(t => t.Id == trabajadorId)
            .Select(t => new TrabajadorParaConector(t.Id, t.TenantId, t.Nombre, t.Apellidos, t.Dni))
            .SingleOrDefaultAsync(cancellationToken);

    public Task<HabilitacionComercialConector?> ObtenerHabilitacionComercialAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        dbContext.Tenants
            .Where(t => t.Id == tenantId)
            .Select(t => new HabilitacionComercialConector(t.AddOnSubidaAutomaticaActivo, t.EstadoComercial, t.Estado))
            .SingleOrDefaultAsync(cancellationToken);

    public void Agregar(ConexionPlataformaCae conexion) => dbContext.ConexionesPlataformaCae.Add(conexion);

    public void AgregarCredencial(CredencialConexionPlataformaCae credencial) => dbContext.CredencialesConexionPlataformaCae.Add(credencial);

    public void QuitarCredencial(CredencialConexionPlataformaCae credencial) => dbContext.CredencialesConexionPlataformaCae.Remove(credencial);

    public void RegistrarUso(UsoConexionPlataformaCae uso) => dbContext.UsosConexionPlataformaCae.Add(uso);

    public void DescartarUso(UsoConexionPlataformaCae uso) => dbContext.Entry(uso).State = EntityState.Detached;

    public bool HayTransaccionAbierta =>
        dbContext.Database.CurrentTransaction is not null || System.Transactions.Transaction.Current is not null;

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
            // Lo que perdió la carrera no se queda pendiente para el próximo SaveChanges del circuito, ni
            // los registros que AuditoriaInterceptor añadió para este guardado fallido.
            foreach (var entrada in dbContext.ChangeTracker.Entries()
                         .Where(e => e.Entity is ConexionPlataformaCae or CredencialConexionPlataformaCae or RegistroAuditoria
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
