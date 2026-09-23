using CaeManager.Domain.Integraciones;

namespace CaeManager.Application.Tests.Integraciones;

public class ReclamacionBuzonIntegracionRepositorioFalso : IReclamacionBuzonIntegracionRepository
{
    public List<ReclamacionBuzonIntegracion> Reclamaciones { get; } = [];
    public int VecesGuardado { get; private set; }

    /// <summary>Si se establece, la próxima <see cref="GuardarCambiosSiBuzonLibreAsync"/> devuelve false — simula el 23505 del índice único.</summary>
    public bool BuzonYaReclamado { get; set; }

    /// <summary>Si se establece, la próxima llamada la lanza en vez de guardar — para probar el manejo de fallos de guardado ajenos a la unicidad de buzón.</summary>
    public Exception? ExcepcionAlGuardar { get; set; }

    public void Reclamar(ReclamacionBuzonIntegracion reclamacion) => Reclamaciones.Add(reclamacion);

    public Task<bool> GuardarCambiosSiBuzonLibreAsync(CancellationToken cancellationToken = default)
    {
        if (ExcepcionAlGuardar is { } excepcion)
            throw excepcion;

        if (BuzonYaReclamado)
        {
            Reclamaciones.Clear(); // Simula dbContext.ChangeTracker.Clear() tras el 23505.
            return Task.FromResult(false);
        }

        VecesGuardado++;
        return Task.FromResult(true);
    }

    public Task LiberarAsync(Guid conexionIntegracionId, CancellationToken cancellationToken = default)
    {
        Reclamaciones.RemoveAll(r => r.ConexionIntegracionId == conexionIntegracionId);
        return Task.CompletedTask;
    }
}
