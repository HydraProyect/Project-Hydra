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
    // Operación guardada → Id de la fila de su marca (la del intento que la guardó).
    private readonly Dictionary<Guid, Guid> _confirmadas = [];
    private OperacionImportacion? _pendiente;
    private Guid? _ultimaGuardada;

    /// <summary>Veces que el handler descartó lo pendiente: un fallo a mitad de construir tiene que llegar aquí.</summary>
    public int VecesDescartado { get; private set; }

    /// <summary>Se ejecuta al guardar, antes de registrar la marca: deja ver qué más iba en el mismo guardado.</summary>
    public Action? AlGuardar { get; set; }

    public Task<bool> ExisteAsync(Guid operacionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_confirmadas.ContainsKey(operacionId));

    public Task<bool> EsLaMarcaGuardadaAsync(Guid operacionId, Guid marcaId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_confirmadas.TryGetValue(operacionId, out var guardada) && guardada == marcaId);

    public void Agregar(OperacionImportacion operacion) => _pendiente = operacion;

    /// <summary>Otra confirmación de la misma operación ganó la carrera y ya la confirmó (con su propia marca).</summary>
    public void ConfirmarDesdeOtraEjecucion(Guid operacionId) => _confirmadas.Add(operacionId, Guid.NewGuid());

    /// <summary>El commit del último guardado falló y la transacción se deshizo: su marca no quedó en la base.</summary>
    public void RevertirUltimoGuardado()
    {
        if (_ultimaGuardada is { } operacionId) _confirmadas.Remove(operacionId);
        _ultimaGuardada = null;
    }

    public Task<bool> GuardarSiOperacionNuevaAsync(CancellationToken cancellationToken = default)
    {
        AlGuardar?.Invoke();
        if (_pendiente is not { } marca || !_confirmadas.TryAdd(marca.OperacionId, marca.Id))
        {
            DescartarPendientes();
            return Task.FromResult(false);
        }

        _ultimaGuardada = marca.OperacionId;
        _pendiente = null;
        return Task.FromResult(true);
    }

    public void DescartarPendientes()
    {
        VecesDescartado++;
        _pendiente = null;
    }
}
