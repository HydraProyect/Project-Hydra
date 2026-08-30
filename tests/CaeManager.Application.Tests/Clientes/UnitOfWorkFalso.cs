using CaeManager.Application.Common;

namespace CaeManager.Application.Tests.Clientes;

public class UnitOfWorkFalso : IUnitOfWork
{
    public int VecesGuardado { get; private set; }

    /// <summary>Si se establece, la próxima llamada a <see cref="SaveChangesAsync"/> la lanza en vez de guardar — para probar el manejo de fallos de guardado.</summary>
    public Exception? ExcepcionAlGuardar { get; set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (ExcepcionAlGuardar is { } excepcion)
            throw excepcion;

        VecesGuardado++;
        return Task.FromResult(1);
    }
}
