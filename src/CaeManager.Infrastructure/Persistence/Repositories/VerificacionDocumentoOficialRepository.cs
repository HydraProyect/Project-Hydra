using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class VerificacionDocumentoOficialRepository(CaeManagerDbContext dbContext) : IVerificacionDocumentoOficialRepository
{
    public void Agregar(VerificacionDocumentoOficial verificacion) =>
        dbContext.VerificacionesDocumentoOficial.Add(verificacion);

    public Task<VerificacionDocumentoOficial?> ObtenerPorDocumentoAsync(
        Guid documentoId, CancellationToken cancellationToken = default) =>
        dbContext.VerificacionesDocumentoOficial
            .FirstOrDefaultAsync(v => v.DocumentoId == documentoId, cancellationToken);

    public void Eliminar(VerificacionDocumentoOficial verificacion) =>
        dbContext.VerificacionesDocumentoOficial.Remove(verificacion);
}
