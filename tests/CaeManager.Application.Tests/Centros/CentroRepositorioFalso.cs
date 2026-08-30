using CaeManager.Domain.Centros;

namespace CaeManager.Application.Tests.Centros;

public class CentroRepositorioFalso : ICentroRepository
{
    public List<Centro> Centros { get; } = [];

    public Task<Centro?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Centros.FirstOrDefault(c => c.Id == id));

    public Task<bool> ExisteConNombreEnClienteAsync(
        Guid clienteId, string nombre, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Centros.Any(c =>
            c.ClienteId == clienteId &&
            string.Equals(c.Nombre, nombre.Trim(), StringComparison.OrdinalIgnoreCase) &&
            c.Id != excluirId));

    public void Agregar(Centro centro) => Centros.Add(centro);
}
