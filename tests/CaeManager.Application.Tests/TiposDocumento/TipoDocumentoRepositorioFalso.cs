using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Tests.TiposDocumento;

public class TipoDocumentoRepositorioFalso : ITipoDocumentoRepository
{
    public List<CaeManager.Domain.Documentos.TipoDocumento> Tipos { get; } = [];

    public Task<CaeManager.Domain.Documentos.TipoDocumento?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Tipos.FirstOrDefault(t => t.Id == id));

    /// <summary>Cuántas veces se pidió lo confirmado: sin contexto que rastree, la propia lista.</summary>
    public int LecturasActualizadas { get; private set; }

    public Task<CaeManager.Domain.Documentos.TipoDocumento?> ObtenerPorIdActualizadoAsync(Guid id, CancellationToken cancellationToken = default)
    {
        LecturasActualizadas++;
        return ObtenerPorIdAsync(id, cancellationToken);
    }

    public Task<bool> ExisteConNombreAsync(string nombre, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Tipos.Any(t => t.Nombre == nombre && t.Id != excluirId));

    public void Agregar(CaeManager.Domain.Documentos.TipoDocumento tipoDocumento) => Tipos.Add(tipoDocumento);
}
