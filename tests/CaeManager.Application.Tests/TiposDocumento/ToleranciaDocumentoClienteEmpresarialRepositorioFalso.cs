using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Tests.TiposDocumento;

public class ToleranciaDocumentoClienteEmpresarialRepositorioFalso : IToleranciaDocumentoClienteEmpresarialRepository
{
    public List<ToleranciaDocumentoClienteEmpresarial> Tolerancias { get; } = [];

    public Task<ToleranciaDocumentoClienteEmpresarial?> ObtenerAsync(
        Guid clienteEmpresarialId, Guid tipoDocumentoId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Tolerancias.FirstOrDefault(t =>
            t.ClienteEmpresarialId == clienteEmpresarialId && t.TipoDocumentoId == tipoDocumentoId));

    public void Agregar(ToleranciaDocumentoClienteEmpresarial tolerancia) => Tolerancias.Add(tolerancia);

    public void Eliminar(ToleranciaDocumentoClienteEmpresarial tolerancia) => Tolerancias.Remove(tolerancia);
}
