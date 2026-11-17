using CaeManager.Application.Inbound.Autorizacion;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Encargo;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Inbound.Requisitos.Queries;

/// <summary>Una plantilla con todos sus requisitos, activos primero. Nula si no existe o no se ve.</summary>
[AreaDelEncargo("TiposDocumento")]
public record ObtenerPlantillaRequisitosPorIdQuery(Guid Id) : IRequest<PlantillaRequisitosDto?>;

public class ObtenerPlantillaRequisitosPorIdQueryHandler(IPlantillasRequisitosQueryContext contexto, IAlcanceDatosService alcanceDatos, IAutorizacionInbound autorizacionInbound)
    : IRequestHandler<ObtenerPlantillaRequisitosPorIdQuery, PlantillaRequisitosDto?>
{
    public async Task<PlantillaRequisitosDto?> Handle(ObtenerPlantillaRequisitosPorIdQuery request, CancellationToken cancellationToken)
    {
        if (!await autorizacionInbound.TieneAsync(CapacidadInbound.LeerInbound, cancellationToken))
            return null;

        var filtro = await PlantillaRequisitosAutorizacion.FiltroDeLecturaAsync(alcanceDatos, cancellationToken);
        if (filtro is null)
            return null;

        var plantilla = await PlantillaRequisitosAutorizacion.AplicarA(contexto.PlantillasRequisitos, filtro)
            .Include(p => p.Requisitos)
            .FirstOrDefaultAsync(p => p.Id == request.Id, cancellationToken);
        if (plantilla is null)
            return null;

        var requisitos = plantilla.Requisitos
            .OrderByDescending(r => r.Activo).ThenBy(r => r.Id)
            .Select(r => new RequisitoPlantillaDto(
                r.Id, r.TipoDocumentoId, r.RequisitoBaseId, r.Entidad, r.Origen, r.ModoVigencia, r.VigenciaMeses, r.ToleranciaDias,
                r.BloqueaAcceso, r.Criterio, r.SePideA, r.TiposContratacion, r.Equivalencias, r.AmbitoDocumento, r.Activo))
            .ToList();

        return new PlantillaRequisitosDto(
            plantilla.Id, plantilla.Tipo, PlantillaRequisitosAutorizacion.ReferenciaDe(plantilla), plantilla.Activa,
            plantilla.Version, requisitos);
    }
}
