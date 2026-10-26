using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Importacion;

namespace CaeManager.Application.Tests.Importacion;

/// <summary>
/// Repositorio de operaciones de importación que delega el guardado en un
/// <see cref="UnitOfWorkFalso"/>, para los tests que ya miden cuántas veces guarda el handler
/// (<see cref="UnitOfWorkFalso.VecesGuardado"/>) y no ejercen la idempotencia: ninguna operación
/// consta como ya confirmada y el guardado siempre es el de una operación nueva.
/// </summary>
internal sealed class OperacionImportacionSobreUnitOfWorkFalso(UnitOfWorkFalso unitOfWork) : IOperacionImportacionRepository
{
    public Task<bool> ExisteAsync(Guid operacionId, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public void Agregar(OperacionImportacion operacion)
    {
    }

    public async Task<bool> GuardarSiOperacionNuevaAsync(CancellationToken cancellationToken = default)
    {
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }

    public void DescartarPendientes()
    {
    }
}
