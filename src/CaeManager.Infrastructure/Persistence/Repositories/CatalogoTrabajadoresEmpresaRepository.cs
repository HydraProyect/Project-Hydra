using CaeManager.Domain.Empresas;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class CatalogoTrabajadoresEmpresaRepository(CaeManagerDbContext dbContext) : ICatalogoTrabajadoresEmpresaRepository
{
    public Task<DelegacionEmpresa?> ObtenerDelegacionEmpresaAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.DelegacionesEmpresa.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

    public Task<OficioEmpresa?> ObtenerOficioAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.OficiosEmpresa.FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

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
}
