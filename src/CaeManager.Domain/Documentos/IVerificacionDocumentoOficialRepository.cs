namespace CaeManager.Domain.Documentos;

public interface IVerificacionDocumentoOficialRepository
{
    void Agregar(VerificacionDocumentoOficial verificacion);

    Task<VerificacionDocumentoOficial?> ObtenerPorDocumentoAsync(
        Guid documentoId, CancellationToken cancellationToken = default);

    /// <summary>Al renovar el archivo, el resultado anterior deja de describir nada: se borra y se recrea con el vigente.</summary>
    void Eliminar(VerificacionDocumentoOficial verificacion);
}
