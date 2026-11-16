using CaeManager.Domain.Inbound.Requisitos;

namespace CaeManager.Application.Inbound.Requisitos;

/// <summary>
/// Lectura de las plantillas de requisitos del Tenant actual (B-14), sin tracking. El filtro global de Tenant y la RLS
/// acotan las filas; la cartera la aplica cada query.
/// </summary>
public interface IPlantillasRequisitosQueryContext
{
    IQueryable<PlantillaRequisitos> PlantillasRequisitos { get; }
    IQueryable<RequisitoPlantilla> RequisitosPlantilla { get; }
}
