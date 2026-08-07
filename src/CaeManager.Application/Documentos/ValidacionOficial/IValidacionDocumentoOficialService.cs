namespace CaeManager.Application.Documentos.ValidacionOficial;

/// <summary>
/// Ejecuta la validación automática de un documento oficial (verificación
/// criptográfica de firma + extracción determinista + cotejo) desde la cola
/// de análisis — ver <see cref="ValidacionDocumentoOficialService"/>.
/// </summary>
public interface IValidacionDocumentoOficialService
{
    Task ProcesarDocumentoAsync(Guid documentoId, CancellationToken cancellationToken = default);
}
