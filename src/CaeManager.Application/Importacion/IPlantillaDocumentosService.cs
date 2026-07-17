namespace CaeManager.Application.Importacion;

/// <summary>
/// Plantilla de importación masiva simplificada para Documentos (DNI, tipo de
/// documento, fecha de emisión) — a diferencia de IExcelImportacionParser, no
/// crea Trabajadores ni Tipos de Documento: ambos deben existir ya en el
/// sistema, solo se suben las fechas de emisión de sus documentos. Reutiliza
/// EjecutarImportacionCommand igual que IPlantillaClientesService.
/// </summary>
public interface IPlantillaDocumentosService
{
    byte[] GenerarPlantilla();

    Task<PlanImportacionDto> AnalizarAsync(Stream archivo, CancellationToken cancellationToken = default);
}
