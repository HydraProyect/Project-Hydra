namespace CaeManager.Infrastructure.HerramientasPdf;

/// <summary>
/// Cuántos procesos aislados corren a la vez. Cada uno puede llegar a su límite de memoria dentro del mismo contenedor que la
/// aplicación, así que el techo de memoria que suman es capacidad × límite. Sirve para cualquier herramienta que abra
/// archivos ajenos en un proceso aparte: quien lo registre decide si comparte la instancia.
/// </summary>
public sealed class LimitadorProcesosAislados(int capacidad)
{
    private readonly SemaphoreSlim _huecos = new(capacidad, capacidad);

    public int Capacidad { get; } = capacidad;

    public int Libres => _huecos.CurrentCount;

    /// <summary>
    /// Un turno que se suelta al desecharlo, o <c>null</c> si no quedó hueco en <paramref name="esperaMaxima"/>. La espera
    /// se corta si el llamante cancela.
    /// </summary>
    public async Task<IDisposable?> EntrarAsync(TimeSpan esperaMaxima, CancellationToken cancellationToken) =>
        await _huecos.WaitAsync(esperaMaxima, cancellationToken) ? new Turno(_huecos) : null;

    private sealed class Turno(SemaphoreSlim huecos) : IDisposable
    {
        private int _soltado;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _soltado, 1) == 0)
                huecos.Release();
        }
    }
}
