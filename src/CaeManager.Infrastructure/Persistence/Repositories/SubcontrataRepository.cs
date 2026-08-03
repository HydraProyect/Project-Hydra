using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Trabajadores;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class SubcontrataRepository(CaeManagerDbContext dbContext) : ISubcontrataRepository
{
    public Task<Subcontrata?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Subcontratas.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public Task<bool> ExisteConRazonSocialAsync(string razonSocial, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
        dbContext.Subcontratas.AnyAsync(
            s => s.RazonSocial.ToUpper() == razonSocial.ToUpper() && (excluirId == null || s.Id != excluirId),
            cancellationToken);

    public Task<bool> ExisteConCifAsync(string cif, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
        dbContext.Subcontratas.AnyAsync(
            s => s.Cif != null && s.Cif.ToUpper() == cif.ToUpper() && (excluirId == null || s.Id != excluirId),
            cancellationToken);

    public Task<bool> TieneTrabajadoresAsync(Guid subcontrataId, CancellationToken cancellationToken = default) =>
        dbContext.Set<Trabajador>().AnyAsync(t => t.SubcontrataId == subcontrataId, cancellationToken);

    public void Agregar(Subcontrata subcontrata) => dbContext.Subcontratas.Add(subcontrata);
}
