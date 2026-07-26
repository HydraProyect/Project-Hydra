namespace CaeManager.Application.Documentos.Verificacion;

public interface IVerificacionIaDocumentoService
{
    Task ProcesarDocumentoAsync(Guid documentoId, CancellationToken cancellationToken = default);
}
