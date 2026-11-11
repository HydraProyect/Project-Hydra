using CaeManager.Application.Importacion.Maestra;

namespace CaeManager.Application.Importacion;

/// <summary>
/// Lector de la plantilla maestra del Importador (Paso 1). Solo analiza: lee el libro, valida por hoja y fila y
/// compara con lo que ya existe en el Tenant propietario, sin escribir nada.
/// </summary>
public interface IPlantillaMaestraService
{
    /// <summary>Libro en blanco de la plantilla maestra (cabeceras y un ejemplo ficticio por hoja) para la descarga de /importacion.</summary>
    byte[] GenerarPlantilla();

    Task<PlanImportacionMaestraDto> AnalizarAsync(Stream archivo, CancellationToken cancellationToken = default);
}
