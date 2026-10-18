using CaeManager.Domain.Empresas;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class PlataformaCaeClienteEmpresarialRepository(CaeManagerDbContext dbContext) : IPlataformaCaeClienteEmpresarialRepository
{
    public Task<PlataformaCaeClienteEmpresarial?> ObtenerPlataformaAsync(Guid clienteEmpresarialId, CancellationToken cancellationToken = default) =>
        dbContext.PlataformasCaeClienteEmpresarial.FirstOrDefaultAsync(p => p.ClienteEmpresarialId == clienteEmpresarialId, cancellationToken);

    public void AgregarPlataforma(PlataformaCaeClienteEmpresarial plataforma) => dbContext.PlataformasCaeClienteEmpresarial.Add(plataforma);

    public Task<AccesoPlataformaCaeEmpresa?> ObtenerAccesoAsync(Guid clienteEmpresarialId, Guid empresaId, CancellationToken cancellationToken = default) =>
        dbContext.AccesosPlataformaCaeEmpresa.FirstOrDefaultAsync(
            a => a.ClienteEmpresarialId == clienteEmpresarialId && a.EmpresaId == empresaId, cancellationToken);

    public void AgregarAcceso(AccesoPlataformaCaeEmpresa acceso) => dbContext.AccesosPlataformaCaeEmpresa.Add(acceso);
}
