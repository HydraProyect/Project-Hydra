namespace CaeManager.Application.Common;

/// <summary>Persiste los cambios hechos a través de los repositorios de agregado.</summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
