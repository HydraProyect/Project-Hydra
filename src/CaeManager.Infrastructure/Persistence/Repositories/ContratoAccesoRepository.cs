using CaeManager.Domain.ContratosAcceso;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

/// <summary>
/// Sin <c>Eliminar</c> físico, a diferencia de <see cref="ProyectoRepository"/>: el contrato de acceso solo se da de baja
/// con soft delete (<c>MarcarComoEliminado</c>), y el rol de runtime no tiene DELETE sobre la tabla.
/// </summary>
public class ContratoAccesoRepository(CaeManagerDbContext dbContext) : IContratoAccesoRepository
{
    public Task<ContratoAcceso?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.ContratosAcceso.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public void Agregar(ContratoAcceso contrato) => dbContext.ContratosAcceso.Add(contrato);
}
