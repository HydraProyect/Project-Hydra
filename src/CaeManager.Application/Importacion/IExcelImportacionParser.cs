namespace CaeManager.Application.Importacion;

/// <summary>
/// Lee un libro Excel con el formato de importación CAE multi-hoja (ver
/// ROADMAP.md, Fase 5) y produce un plan clasificado, sin escribir nada en
/// la base de datos todavía. La implementación real vive en Infrastructure
/// (usa ClosedXML, un detalle de formato de archivo que Application no
/// necesita conocer).
/// </summary>
public interface IExcelImportacionParser
{
    Task<PlanImportacionDto> AnalizarAsync(Stream archivo, CancellationToken cancellationToken = default);
}
