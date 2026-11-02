using CaeManager.Application.Importacion.Maestra;

namespace CaeManager.Application.Importacion;

/// <summary>
/// Lector de la plantilla maestra del Importador (Paso 1). Solo analiza: lee el libro, valida por hoja y fila y
/// compara con lo que ya existe en el Tenant propietario, sin escribir nada.
/// </summary>
public interface IPlantillaMaestraService
{
    Task<PlanImportacionMaestraDto> AnalizarAsync(Stream archivo, CancellationToken cancellationToken = default);
}
