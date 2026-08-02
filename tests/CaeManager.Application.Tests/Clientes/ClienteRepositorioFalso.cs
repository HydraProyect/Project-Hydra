using CaeManager.Domain.Clientes;

namespace CaeManager.Application.Tests.Clientes;

/// <summary>Fake en memoria — los handlers de Application se prueban sin base de datos (ver CODING_STANDARDS.md).</summary>
public class ClienteRepositorioFalso : IClienteRepository
{
    public List<Cliente> Clientes { get; } = [];
    public bool TieneCentrosActivos { get; set; }

    /// <summary>Control fino por id, para probar éxito parcial en borrado en lote (P3-31) sin afectar <see cref="TieneCentrosActivos"/>.</summary>
    public HashSet<Guid> IdsConCentrosActivos { get; } = [];

    public Task<Cliente?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Clientes.FirstOrDefault(c => c.Id == id));

    public Task<bool> ExisteConRazonSocialAsync(string razonSocial, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Clientes.Any(c => c.RazonSocial == razonSocial && c.Id != excluirId));

    public Task<bool> ExisteConCifAsync(string cif, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Clientes.Any(c => c.Cif == cif && c.Id != excluirId));

    public Task<bool> TieneCentrosActivosAsync(Guid clienteId, CancellationToken cancellationToken = default) =>
        Task.FromResult(TieneCentrosActivos || IdsConCentrosActivos.Contains(clienteId));

    public void Agregar(Cliente cliente) => Clientes.Add(cliente);
}
