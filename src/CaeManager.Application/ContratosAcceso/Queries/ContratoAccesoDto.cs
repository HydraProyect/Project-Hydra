namespace CaeManager.Application.ContratosAcceso.Queries;

/// <param name="Notas">Solo para quien lee la «Nota interna» (<c>IPoliticaNotaInterna</c>), igual que las notas de Proyecto; null para el resto.</param>
/// <param name="VigenteHoy">El criterio de <c>ContratoAcceso.VigenteEn</c> evaluado en el día de negocio de hoy.</param>
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
    Guid Version);
