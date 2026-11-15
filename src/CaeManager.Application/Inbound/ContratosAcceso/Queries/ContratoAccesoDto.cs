using CaeManager.Domain.Inbound.ContratosAcceso;

namespace CaeManager.Application.Inbound.ContratosAcceso.Queries;

/// <param name="Notas">Solo para quien lee la «Nota interna» (<c>IPoliticaNotaInterna</c>), igual que las notas de Proyecto; null para el resto.</param>
/// <param name="VigenteHoy">El criterio de <c>ContratoAcceso.VigenteEn</c> evaluado en el día de negocio de hoy.</param>
/// <param name="Estado">El estado del ciclo de vida (B-05).</param>
/// <param name="CupoTrabajadores">Null = sin límite; igual en <paramref name="CupoSubcontratas"/>.</param>
public record ContratoAccesoDto(
    Guid Id,
    Guid ClienteEmpresarialId,
    Guid CentroId,
    Guid EmpresaContratistaId,
    DateOnly FechaInicio,
    DateOnly? FechaFin,
    DateOnly? FechaCierreReal,
    bool EstaAbierto,
    bool VigenteHoy,
    string? Notas,
    Guid Version,
    EstadoContratoAcceso Estado,
    TipoContratoAcceso? Tipo,
    PropiaActividadContrato? PropiaActividad,
    int? NivelMaxSubcontrata,
    int? CupoTrabajadores,
    int? CupoSubcontratas,
    string? MotivoCupo)
{
    public static ContratoAccesoDto Desde(ContratoAcceso c, DateOnly hoy, bool conNotaInterna) =>
        new(c.Id, c.ClienteEmpresarialId, c.CentroId, c.EmpresaContratistaId, c.FechaInicio, c.FechaFin, c.FechaCierreReal,
            c.EstaAbierto, c.VigenteEn(hoy), conNotaInterna ? c.Notas : null, c.Version,
            c.Estado, c.Tipo, c.PropiaActividad, c.NivelMaxSubcontrata, c.CupoTrabajadores, c.CupoSubcontratas, c.MotivoCupo);
}
