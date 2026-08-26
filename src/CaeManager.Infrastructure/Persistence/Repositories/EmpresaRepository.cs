using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class EmpresaRepository(CaeManagerDbContext dbContext) : IEmpresaRepository
{
    public Task<Empresa?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Empresas.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public Task<bool> ExisteConRazonSocialAsync(string razonSocial, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
        dbContext.Empresas.AnyAsync(
            e => e.RazonSocial.ToUpper() == razonSocial.ToUpper() && (excluirId == null || e.Id != excluirId),
            cancellationToken);

    public Task<bool> ExisteConCifAsync(string cif, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
        dbContext.Empresas.AnyAsync(
            e => e.Cif != null && e.Cif.ToUpper() == cif.ToUpper() && (excluirId == null || e.Id != excluirId),
            cancellationToken);

    public Task<bool> TieneTrabajadoresAsync(Guid empresaId, CancellationToken cancellationToken = default) =>
        dbContext.Set<Trabajador>().AnyAsync(t => t.EmpresaId == empresaId, cancellationToken);

    public Task<bool> TieneCentrosComoTitularAsync(Guid empresaId, CancellationToken cancellationToken = default) =>
        dbContext.Set<Centro>().AnyAsync(c => c.ClienteId == empresaId, cancellationToken);

    public void Agregar(Empresa empresa) => dbContext.Empresas.Add(empresa);
}
