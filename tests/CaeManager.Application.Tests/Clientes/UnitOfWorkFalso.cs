using CaeManager.Application.Common;

namespace CaeManager.Application.Tests.Clientes;

public class UnitOfWorkFalso : IUnitOfWork
{
    public int VecesGuardado { get; private set; }

    /// <summary>Si se establece, la próxima llamada a <see cref="SaveChangesAsync"/> la lanza en vez de guardar — para probar el manejo de fallos de guardado.</summary>
    public Exception? ExcepcionAlGuardar { get; set; }

    /// <summary>
    /// Si se establece, corre dentro de cada <see cref="SaveChangesAsync"/>: para fotografiar qué había agregado en el
    /// momento del guardado, y no después, cuando lo agregado tarde seguiría en memoria igual.
    /// </summary>
    public Action? AlGuardar { get; set; }

    /// <summary>El token con el que se pidió cada guardado, también los que lanzaron: para fijar cuáles no son cancelables.</summary>
    public List<CancellationToken> TokensDeGuardado { get; } = [];

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        TokensDeGuardado.Add(cancellationToken);
        if (ExcepcionAlGuardar is { } excepcion)
            throw excepcion;

        AlGuardar?.Invoke();
        VecesGuardado++;
        return Task.FromResult(1);
    }
}
