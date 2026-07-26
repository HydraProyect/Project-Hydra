namespace CaeManager.Application.Importacion;

/// <summary>
/// Plantilla de importación masiva simplificada, solo para Cliente/Centro
/// (a diferencia de IExcelImportacionParser, que espera el formato completo
/// multi-hoja de importación CAE). Pensada para dar de alta rápidamente el
/// resto de clientes de la cartera sin tener que rellenar un libro con la
/// estructura completa. Reutiliza EjecutarImportacionCommand para escribir
/// el plan resultante — el comando ya trata ClientesCentros de forma genérica.
/// </summary>
public interface IPlantillaClientesService
{
    byte[] GenerarPlantilla();

    Task<PlanImportacionDto> AnalizarAsync(Stream archivo, CancellationToken cancellationToken = default);
}
