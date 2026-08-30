using CaeManager.Domain.Proyectos;

namespace CaeManager.Application.Tests.Proyectos;

public class ProyectoRepositorioFalso : IProyectoRepository
{
    public List<Proyecto> Proyectos { get; } = [];

    public Task<Proyecto?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Proyectos.FirstOrDefault(p => p.Id == id));

    public Task<bool> ExisteNombreParaClienteAsync(
        Guid clienteId, string nombre, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Proyectos.Any(p =>
            p.ClienteId == clienteId &&
            string.Equals(p.Nombre, nombre.Trim(), StringComparison.Ordinal) &&
            p.Id != excluirId));

    public void Agregar(Proyecto proyecto) => Proyectos.Add(proyecto);

    public void Eliminar(Proyecto proyecto) => Proyectos.Remove(proyecto);
}
