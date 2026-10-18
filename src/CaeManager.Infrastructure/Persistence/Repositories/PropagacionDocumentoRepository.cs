using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public sealed class PropagacionDocumentoRepository(CaeManagerDbContext contexto) : IPropagacionDocumentoRepository
{
    public async Task<PropagacionDocumento?> ObtenerAsync(Guid documentoId, Guid canalId, CancellationToken cancellationToken)
    {
        var fila = await contexto.Set<PropagacionDocumento>().SingleOrDefaultAsync(
            p => p.DocumentoId == documentoId && p.CanalGestionDocumentalId == canalId, cancellationToken);
        if (fila is not null) await contexto.Entry(fila).ReloadAsync(cancellationToken);
        return fila;
    }

    public void Agregar(PropagacionDocumento propagacion) => contexto.Set<PropagacionDocumento>().Add(propagacion);
}
