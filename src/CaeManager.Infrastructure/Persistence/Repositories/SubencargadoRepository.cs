using CaeManager.Domain.Cumplimiento.Subencargados;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Seguridad;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

/// <summary>
/// <c>Subencargados</c> es un catálogo de Plataforma que todo el runtime lee y que solo escribe un Actor de Plataforma
/// TALVEG (lo aplica la RLS). Los avisos y las objeciones son del Tenant actual: filtro global de Tenant y RLS. Ninguna
/// lectura cruza Tenants; para recorrer varios, quien llama abre el ámbito de cada uno.
/// </summary>
public class SubencargadoRepository(CaeManagerDbContext dbContext) : ISubencargadoRepository
{
    public void Agregar(Subencargado subencargado) => dbContext.Subencargados.Add(subencargado);

    public Task<Subencargado?> ObtenerAsync(Guid subencargadoId, CancellationToken cancellationToken = default) =>
        dbContext.Subencargados.FirstOrDefaultAsync(s => s.Id == subencargadoId, cancellationToken);

    public async Task<IReadOnlyList<Guid>> TenantsPropietariosActivosAsync(CancellationToken cancellationToken = default) =>
        await dbContext.Tenants.AsNoTracking()
            .Where(t => t.Estado == EstadoTenant.Activo && !t.EsPlataforma)
            .OrderBy(t => t.Id)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);

    public Task<bool> TieneAvisoProbadoAsync(Guid subencargadoId, DateTime? enviadoComoTardeEnUtc, CancellationToken cancellationToken = default) =>
        dbContext.AvisosCambioSubencargado.AsNoTracking().AnyAsync(
            a => a.SubencargadoId == subencargadoId
                 && a.Fase == FaseAvisoCambioSubencargado.Enviado
                 && (enviadoComoTardeEnUtc == null || a.RegistradoEnUtc <= enviadoComoTardeEnUtc),
            cancellationToken);

    public async Task<IReadOnlyList<DestinatarioAvisoSubencargado>> AdministradoresDelTenantActualAsync(
        Guid tenantId, CancellationToken cancellationToken = default) =>
        (await VigilanciaInicioSesionService.AdministradoresAsync(
            dbContext, Roles.Administrador.ToUpperInvariant(), tenantId, usuarioId: null, cancellationToken))
        .Where(a => !string.IsNullOrWhiteSpace(a.Email))
        .Select(a => new DestinatarioAvisoSubencargado(a.Id, a.Email!))
        .ToList();

    public void AgregarAviso(AvisoCambioSubencargado aviso) => dbContext.AvisosCambioSubencargado.Add(aviso);

    public Task<bool> ExisteObjecionAsync(Guid subencargadoId, CancellationToken cancellationToken = default) =>
        dbContext.ObjecionesSubencargado.AsNoTracking().AnyAsync(o => o.SubencargadoId == subencargadoId, cancellationToken);

    public void AgregarObjecion(ObjecionSubencargado objecion) => dbContext.ObjecionesSubencargado.Add(objecion);

    public Task<bool> ExcluyeConmutadorAsync(
        Guid tenantId, ConmutadorExclusionSubencargado conmutador, CancellationToken cancellationToken = default) =>
        dbContext.ObjecionesSubencargado.AsNoTracking().AnyAsync(
            o => o.TenantId == tenantId
                 && o.Efecto == EfectoObjecionSubencargado.ExcluidoParaElTenant
                 && o.Conmutador == conmutador
                 && dbContext.Subencargados.Any(s => s.Id == o.SubencargadoId && s.Estado == EstadoSubencargado.Activo),
            cancellationToken);

    public bool HayTransaccionAbierta =>
        dbContext.Database.CurrentTransaction is not null || System.Transactions.Transaction.Current is not null;

    public bool HayCambiosPendientes => dbContext.ChangeTracker.HasChanges();
}
