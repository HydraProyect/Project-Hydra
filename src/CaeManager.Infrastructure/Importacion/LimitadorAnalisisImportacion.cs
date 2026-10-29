using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Importacion;

public sealed class LimitadorAnalisisImportacion : IDisposable
{
    private readonly SemaphoreSlim _semaforo;
    private readonly TimeSpan _espera;

    public LimitadorAnalisisImportacion(IOptions<OpcionesLimitesImportacion> opciones)
    {
        opciones.Value.Validar();
        _semaforo = new SemaphoreSlim(opciones.Value.AnalisisConcurrentes);
        _espera = TimeSpan.FromSeconds(opciones.Value.EsperaSegundos);
    }

    public async Task<IDisposable?> IntentarEntrarAsync(CancellationToken cancellationToken = default) =>
        await _semaforo.WaitAsync(_espera, cancellationToken) ? new Permiso(_semaforo) : null;

    public void Dispose() => _semaforo.Dispose();

    private sealed class Permiso(SemaphoreSlim semaforo) : IDisposable
    {
        private SemaphoreSlim? _semaforo = semaforo;
        public void Dispose() => Interlocked.Exchange(ref _semaforo, null)?.Release();
    }
}
