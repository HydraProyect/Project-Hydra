namespace CaeManager.Application.Trabajadores.Deteccion;

/// <summary>
/// Orquesta la detección de altas/bajas de personal a partir de un
/// Documento de Empresa recién subido — ver DeteccionTrabajadoresService
/// para la lógica real. Es "mejor esfuerzo": nunca lanza, y su fallo nunca
/// debe impedir que la subida del propio Documento se complete (ver
/// CrearDocumentoCommandHandler, que la llama después de guardar).
/// </summary>
public interface IDeteccionTrabajadoresService
{
    Task ProcesarDocumentoAsync(Guid documentoId, CancellationToken cancellationToken = default);
}
