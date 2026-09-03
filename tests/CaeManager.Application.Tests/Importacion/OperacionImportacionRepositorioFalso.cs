using CaeManager.Domain.Importacion;

namespace CaeManager.Application.Tests.Importacion;

/// <summary>
/// Fake en memoria de <see cref="IOperacionImportacionRepository"/>. Modela la
/// única propiedad que le importa a EjecutarImportacionCommandHandler: una
/// operación ya registrada no puede volver a "guardarse" — <see cref="GuardarSiOperacionNuevaAsync"/>
/// devuelve <c>false</c> igual que lo haría el índice único real de Postgres si
/// <see cref="Agregar"/> ya añadió esa <c>OperacionId</c> antes. No simula la
/// carrera concurrente real (eso exige Postgres — ver
/// EjecutarImportacionConcurrenciaTests en IntegrationTests), solo el camino
/// secuencial: misma operación, dos confirmaciones seguidas.
/// </summary>
internal sealed class OperacionImportacionRepositorioFalso : IOperacionImportacionRepository
{
    private readonly HashSet<Guid> _confirmadas = [];
    private Guid? _pendiente;

    public Task<bool> ExisteAsync(Guid operacionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_confirmadas.Contains(operacionId));

    public void Agregar(OperacionImportacion operacion) => _pendiente = operacion.OperacionId;

    public Task<bool> GuardarSiOperacionNuevaAsync(CancellationToken cancellationToken = default)
    {
        if (_pendiente is not { } operacionId || !_confirmadas.Add(operacionId))
        {
            DescartarPendientes();
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    public void DescartarPendientes() => _pendiente = null;
}
