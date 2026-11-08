using CaeManager.Domain.Asignaciones;

namespace CaeManager.Application.Tests.Asignaciones;

public class AsignacionRepositorioFalso : IAsignacionRepository
{
    public List<Asignacion> Asignaciones { get; } = [];

    public Task<Asignacion?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Asignaciones.FirstOrDefault(a => a.Id == id));

    /// <summary>Cuántas veces se pidió lo confirmado: sin contexto que rastree, la propia lista.</summary>
    public int LecturasActualizadas { get; private set; }

    public Task<Asignacion?> ObtenerPorIdActualizadoAsync(Guid id, CancellationToken cancellationToken = default)
    {
        LecturasActualizadas++;
        return ObtenerPorIdAsync(id, cancellationToken);
    }

    public Task<bool> ExisteActivaAsync(Guid trabajadorId, Guid centroId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Asignaciones.Any(a => a.TrabajadorId == trabajadorId && a.CentroId == centroId && a.FechaBaja is null));

    public Task<bool> ExisteSolapeAsync(
        Guid trabajadorId, Guid centroId, DateOnly fechaAlta, DateOnly? fechaBaja, CancellationToken cancellationToken = default) =>
        Task.FromResult(Asignaciones.Any(a =>
            a.TrabajadorId == trabajadorId && a.CentroId == centroId && a.SeSolapaCon(fechaAlta, fechaBaja)));

    public Task<bool> ExisteSolapeConOtraAsync(
        Guid asignacionId, Guid trabajadorId, Guid centroId, DateOnly fechaAlta, DateOnly? fechaBaja,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Asignaciones.Any(a =>
            a.Id != asignacionId && a.TrabajadorId == trabajadorId && a.CentroId == centroId
            && a.SeSolapaCon(fechaAlta, fechaBaja)));

    public Task<IReadOnlyList<Asignacion>> ObtenerActivasPorCentroAsync(Guid centroId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Asignacion>>(
            Asignaciones.Where(a => a.CentroId == centroId && a.FechaBaja is null).ToList());

    public Task<IReadOnlyList<Asignacion>> ObtenerActivasPorTrabajadorAsync(Guid trabajadorId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Asignacion>>(
            Asignaciones.Where(a => a.TrabajadorId == trabajadorId && a.FechaBaja is null).ToList());

    public void Agregar(Asignacion asignacion) => Asignaciones.Add(asignacion);
}
