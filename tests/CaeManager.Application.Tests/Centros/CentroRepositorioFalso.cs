using CaeManager.Domain.Centros;

namespace CaeManager.Application.Tests.Centros;

public class CentroRepositorioFalso : ICentroRepository
{
    public List<Centro> Centros { get; } = [];

    public Task<Centro?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Centros.FirstOrDefault(c => c.Id == id));

    /// <summary>
    /// Lo que otra operación confirma en la base mientras se espera el candado. Solo lo ven las lecturas «…Actualizado»,
    /// igual que EF, que no repone una instancia ya rastreada salvo que se relea.
    /// </summary>
    public Action<Centro>? CambioEnLaBase { get; set; }

    public Task<Centro?> ObtenerIncluidoEliminadoActualizadoAsync(Guid id, Guid? tenantId, CancellationToken cancellationToken = default)
    {
        var centro = Centros.FirstOrDefault(c => c.Id == id && c.TenantId == tenantId);
        if (centro is not null)
            CambioEnLaBase?.Invoke(centro);
        return Task.FromResult(centro);
    }

    public Task<IReadOnlyList<Centro>> ListarActualizadosAsync(CancellationToken cancellationToken = default)
    {
        foreach (var centro in Centros)
            CambioEnLaBase?.Invoke(centro);
        return Task.FromResult<IReadOnlyList<Centro>>(Centros.Where(c => !c.EstaEliminado).ToList());
    }

    public Task<bool> ExisteConNombreEnClienteAsync(
        Guid clienteId, string nombre, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Centros.Any(c =>
            c.ClienteId == clienteId &&
            string.Equals(c.Nombre, nombre.Trim(), StringComparison.OrdinalIgnoreCase) &&
            c.Id != excluirId));

    public void Agregar(Centro centro) => Centros.Add(centro);
}
