using CaeManager.Domain.Inbound.Requisitos;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Inbound.Persistence;

/// <summary>
/// Sin <c>Eliminar</c>: una plantilla y sus requisitos se retiran, no se borran, y el runtime no tiene DELETE sobre las
/// tablas.
/// </summary>
public class PlantillaRequisitosRepository(CaeManagerDbContext dbContext) : IPlantillaRequisitosRepository
{
    public Task<PlantillaRequisitos?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.PlantillasRequisitos
            .Include(p => p.Requisitos)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<bool> ExisteActivaAsync(TipoPlantillaRequisitos tipo, Guid referenciaId, CancellationToken cancellationToken = default)
    {
        var activas = dbContext.PlantillasRequisitos.Where(p => p.Activa);
        return tipo switch
        {
            TipoPlantillaRequisitos.PorActividad => activas.AnyAsync(p => p.ActividadId == referenciaId, cancellationToken),
            TipoPlantillaRequisitos.PorRiesgo => activas.AnyAsync(p => p.RiesgoId == referenciaId, cancellationToken),
            TipoPlantillaRequisitos.DeCentro => activas.AnyAsync(p => p.CentroId == referenciaId, cancellationToken),
            TipoPlantillaRequisitos.ExigenciasClienteEmpresarial =>
                activas.AnyAsync(p => p.ClienteEmpresarialId == referenciaId, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(tipo), tipo, null),
        };
    }

    public void Agregar(PlantillaRequisitos plantilla) => dbContext.PlantillasRequisitos.Add(plantilla);
}
