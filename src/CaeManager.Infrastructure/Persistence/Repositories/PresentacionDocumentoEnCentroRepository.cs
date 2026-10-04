using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class PresentacionDocumentoEnCentroRepository(CaeManagerDbContext dbContext) : IPresentacionDocumentoEnCentroRepository
{
    public Task<bool> ExisteAsync(
        Guid documentoId, Guid centroId, DateOnly fechaPresentacion, OrigenPresentacionDocumentoEnCentro origen,
        CancellationToken cancellationToken = default) =>
        dbContext.PresentacionesDocumentoEnCentro.AnyAsync(
            p => p.DocumentoId == documentoId && p.CentroId == centroId && p.FechaPresentacion == fechaPresentacion && p.Origen == origen,
            cancellationToken);

    public void Agregar(PresentacionDocumentoEnCentro presentacion) =>
        dbContext.PresentacionesDocumentoEnCentro.Add(presentacion);
}
