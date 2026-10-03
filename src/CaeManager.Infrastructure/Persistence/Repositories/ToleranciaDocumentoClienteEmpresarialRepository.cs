using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ToleranciaDocumentoClienteEmpresarialRepository(CaeManagerDbContext dbContext)
    : IToleranciaDocumentoClienteEmpresarialRepository
{
    public Task<ToleranciaDocumentoClienteEmpresarial?> ObtenerAsync(
        Guid clienteEmpresarialId, Guid tipoDocumentoId, CancellationToken cancellationToken = default) =>
        dbContext.ToleranciasDocumentoClienteEmpresarial
            .FirstOrDefaultAsync(t => t.ClienteEmpresarialId == clienteEmpresarialId && t.TipoDocumentoId == tipoDocumentoId, cancellationToken);

    public void Agregar(ToleranciaDocumentoClienteEmpresarial tolerancia) =>
        dbContext.ToleranciasDocumentoClienteEmpresarial.Add(tolerancia);

    public void Eliminar(ToleranciaDocumentoClienteEmpresarial tolerancia) =>
        dbContext.ToleranciasDocumentoClienteEmpresarial.Remove(tolerancia);
}
