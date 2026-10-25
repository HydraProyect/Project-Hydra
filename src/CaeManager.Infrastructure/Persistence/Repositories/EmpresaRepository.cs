using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class EmpresaRepository(CaeManagerDbContext dbContext) : IEmpresaRepository
{
    public Task<Empresa?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.ObtenerActualAsync(dbContext.Empresas.Where(e => e.Id == id), cancellationToken);

    public Task<bool> ExisteConRazonSocialAsync(string razonSocial, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
        dbContext.Empresas.AnyAsync(
            e => e.RazonSocial.ToUpper() == razonSocial.ToUpper() && (excluirId == null || e.Id != excluirId),
            cancellationToken);

    public async Task<bool> ExisteConCifAsync(string cif, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
        await ObtenerDuplicadaPorCifAsync(cif, excluirId, cancellationToken) is not null;

    public Task<Empresa?> ObtenerDuplicadaPorCifAsync(string cif, Guid? excluirId = null, CancellationToken cancellationToken = default)
    {
        var normalizado = cif.Trim().ToUpperInvariant();
        // El índice incluye bajas. Al retirar el filtro combinado, se repone explícitamente el Tenant
        // propietario; sin Tenant resuelto no se ve ninguna fila. RLS sigue aplicándose.
        return dbContext.Empresas.IgnoreQueryFilters().AsNoTracking()
            .Where(e => dbContext.TenantPropietarioId != null && e.TenantId == dbContext.TenantPropietarioId)
            .FirstOrDefaultAsync(e => e.Cif == normalizado && (excluirId == null || e.Id != excluirId), cancellationToken);
    }

    public Task<bool> TieneTrabajadoresAsync(Guid empresaId, CancellationToken cancellationToken = default) =>
        dbContext.Set<Trabajador>().AnyAsync(t => t.EmpresaId == empresaId, cancellationToken);

    public Task<bool> TieneCentrosComoTitularAsync(Guid empresaId, CancellationToken cancellationToken = default) =>
        dbContext.Set<Centro>().AnyAsync(c => c.ClienteId == empresaId, cancellationToken);

    public Task<bool> TieneTrabajadoresComoSubcontrataAsync(Guid empresaId, CancellationToken cancellationToken = default) =>
        dbContext.Set<Trabajador>().AnyAsync(t => t.SubcontrataId == empresaId, cancellationToken);

    public void Agregar(Empresa empresa) => dbContext.Empresas.Add(empresa);
}
