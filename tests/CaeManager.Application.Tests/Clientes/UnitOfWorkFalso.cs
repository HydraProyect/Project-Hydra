using CaeManager.Application.Common;

namespace CaeManager.Application.Tests.Clientes;

public class UnitOfWorkFalso : IUnitOfWork
{
    public int VecesGuardado { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        VecesGuardado++;
        return Task.FromResult(1);
    }
}
