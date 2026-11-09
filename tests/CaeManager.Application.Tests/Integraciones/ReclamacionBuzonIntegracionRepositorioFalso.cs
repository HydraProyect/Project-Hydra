using CaeManager.Domain.Integraciones;

namespace CaeManager.Application.Tests.Integraciones;

public class ReclamacionBuzonIntegracionRepositorioFalso : IReclamacionBuzonIntegracionRepository
{
    public List<ReclamacionBuzonIntegracion> Reclamaciones { get; } = [];
    public int VecesGuardado { get; private set; }

    /// <summary>Reclamaciones añadidas desde el último guardado con éxito (las que un guardado fallido descarta).</summary>
    private readonly List<ReclamacionBuzonIntegracion> _sinGuardar = [];

    /// <summary>Si se establece, la próxima <see cref="GuardarCambiosSiBuzonLibreAsync"/> devuelve BuzonYaReclamado — simula el 23505 del índice único.</summary>
    public bool BuzonYaReclamado { get; set; }

    /// <summary>Si se establece, la próxima llamada la lanza en vez de guardar — para probar el manejo de fallos de guardado ajenos a la unicidad de buzón.</summary>
    public Exception? ExcepcionAlGuardar { get; set; }

    public void Reclamar(ReclamacionBuzonIntegracion reclamacion)
    {
        Reclamaciones.Add(reclamacion);
        _sinGuardar.Add(reclamacion);
    }

    /// <summary>
    /// Como <see cref="ConexionIntegracionRepositorioFalso.SimularRastreo"/>, para las reclamaciones: con él,
    /// <see cref="ObtenerPorConexionAsync"/> devuelve la instancia que ya devolvió antes aunque la «base»
    /// (<see cref="Reclamaciones"/>) tenga otra, y solo <see cref="ObtenerActualPorConexionAsync"/> lee la de la base.
    /// </summary>
    public bool SimularRastreo { get; set; }

    private readonly Dictionary<Guid, ReclamacionBuzonIntegracion> _rastreadas = [];

    public Task<ReclamacionBuzonIntegracion?> ObtenerPorConexionAsync(Guid conexionIntegracionId, CancellationToken cancellationToken = default)
    {
        if (SimularRastreo && _rastreadas.TryGetValue(conexionIntegracionId, out var rastreada))
            return Task.FromResult<ReclamacionBuzonIntegracion?>(rastreada);

        return Task.FromResult(Rastrear(Reclamaciones.FirstOrDefault(r => r.ConexionIntegracionId == conexionIntegracionId)));
    }

    public Task<ReclamacionBuzonIntegracion?> ObtenerActualPorConexionAsync(Guid conexionIntegracionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Rastrear(Reclamaciones.FirstOrDefault(r => r.ConexionIntegracionId == conexionIntegracionId)));

    private ReclamacionBuzonIntegracion? Rastrear(ReclamacionBuzonIntegracion? reclamacion)
    {
        if (SimularRastreo && reclamacion is not null)
            _rastreadas[reclamacion.ConexionIntegracionId] = reclamacion;
        return reclamacion;
    }

    /// <summary>
    /// Otra operación guardó otra reclamación de la conexión (p. ej. Desconectar la liberó y Reactivar la volvió a
    /// reclamar): la «base» pasa a tener otra instancia y la que el llamador tenía rastreada queda obsoleta.
    /// </summary>
    public ReclamacionBuzonIntegracion SustituirEnBase(Guid conexionIntegracionId)
    {
        var indice = Reclamaciones.FindIndex(r => r.ConexionIntegracionId == conexionIntegracionId);
        var original = Reclamaciones[indice];
        var nueva = new ReclamacionBuzonIntegracion(
            original.BuzonEmail, original.IdentidadBuzon, original.TenantPropietarioId, conexionIntegracionId, DateTime.UtcNow,
            esBuzonCompartido: original.EsBuzonCompartido);
        Reclamaciones[indice] = nueva;
        return nueva;
    }

    public Task<IReadOnlyList<ReclamacionBuzonIntegracion>> ObtenerQueCuentanParaGuardaTransitoriaAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ReclamacionBuzonIntegracion>>(Reclamaciones.Where(r => r.CuentaParaGuardaTransitoria).ToList());

    public Task<ResultadoReclamacionBuzon> GuardarCambiosSiBuzonLibreAsync(CancellationToken cancellationToken = default)
    {
        if (ExcepcionAlGuardar is { } excepcion)
            throw excepcion;

        if (BuzonYaReclamado)
        {
            Reclamaciones.Clear(); // Simula dbContext.ChangeTracker.Clear() tras el 23505.
            _sinGuardar.Clear();
            return Task.FromResult(ResultadoReclamacionBuzon.BuzonYaReclamado);
        }

        // La restricción de exclusión de la migración 20261109210000, en memoria: una compartida y una heredada que
        // cuenta no conviven. El guardado fallido descarta lo que no se había guardado.
        if (Reclamaciones.Any(r => r.EsBuzonCompartido) && Reclamaciones.Any(r => r.CuentaParaGuardaTransitoria))
        {
            Reclamaciones.RemoveAll(_sinGuardar.Contains);
            _sinGuardar.Clear();
            return Task.FromResult(ResultadoReclamacionBuzon.AltaCompartidaEnPausa);
        }

        _sinGuardar.Clear();
        VecesGuardado++;
        return Task.FromResult(ResultadoReclamacionBuzon.Guardada);
    }

    public Task LiberarAsync(Guid conexionIntegracionId, CancellationToken cancellationToken = default)
    {
        Reclamaciones.RemoveAll(r => r.ConexionIntegracionId == conexionIntegracionId);
        return Task.CompletedTask;
    }

    /// <summary>Una reclamación anterior a IdentidadBuzon: sin identidad y dentro de la guarda (como tras la migración).</summary>
    public ReclamacionBuzonIntegracion AgregarHeredada(string direccion, Guid conexionId)
    {
        var reclamacion = new ReclamacionBuzonIntegracion(direccion, null, Guid.NewGuid(), conexionId, DateTime.UtcNow);
        typeof(ReclamacionBuzonIntegracion).GetProperty(nameof(ReclamacionBuzonIntegracion.FueraDeGuardaTransitoriaUtc))!
            .SetValue(reclamacion, null);
        Reclamaciones.Add(reclamacion);
        return reclamacion;
    }
}
