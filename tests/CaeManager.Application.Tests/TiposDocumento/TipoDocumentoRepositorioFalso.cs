using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Tests.TiposDocumento;

public class TipoDocumentoRepositorioFalso : ITipoDocumentoRepository
{
    public List<CaeManager.Domain.Documentos.TipoDocumento> Tipos { get; } = [];

    public Task<CaeManager.Domain.Documentos.TipoDocumento?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Tipos.FirstOrDefault(t => t.Id == id));

    public Task<bool> ExisteConNombreAsync(string nombre, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Tipos.Any(t => t.Nombre == nombre && t.Id != excluirId));

    public void Agregar(CaeManager.Domain.Documentos.TipoDocumento tipoDocumento) => Tipos.Add(tipoDocumento);
}
