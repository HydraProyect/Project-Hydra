using CaeManager.Domain.Trabajadores;

namespace CaeManager.Application.Tests.Trabajadores;

public class TrabajadorRepositorioFalso : ITrabajadorRepository
{
    public List<Trabajador> Trabajadores { get; } = [];

    public Task<Trabajador?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Trabajadores.FirstOrDefault(t => t.Id == id));

    public Task<bool> ExisteConDniAsync(string dni, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Trabajadores.Any(t => t.Dni == dni.Trim().ToUpperInvariant() && t.Id != excluirId));

    /// <summary>DNI de Trabajadores que están en la Papelera del Tenant propietario (el fake de lectura no ve eliminados).</summary>
    public HashSet<string> DnisEnPapelera { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlySet<string>> ObtenerDnisEnPapeleraAsync(IReadOnlyCollection<string> dnis, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(
            dnis.Where(DnisEnPapelera.Contains), StringComparer.OrdinalIgnoreCase));

    public void Agregar(Trabajador trabajador) => Trabajadores.Add(trabajador);
}
