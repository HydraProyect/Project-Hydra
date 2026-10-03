namespace CaeManager.Domain.Documentos;

public interface IToleranciaDocumentoClienteEmpresarialRepository
{
    Task<ToleranciaDocumentoClienteEmpresarial?> ObtenerAsync(
        Guid clienteEmpresarialId, Guid tipoDocumentoId, CancellationToken cancellationToken = default);

    void Agregar(ToleranciaDocumentoClienteEmpresarial tolerancia);

    void Eliminar(ToleranciaDocumentoClienteEmpresarial tolerancia);
}
