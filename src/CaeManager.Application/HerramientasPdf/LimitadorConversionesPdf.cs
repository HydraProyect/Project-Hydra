namespace CaeManager.Application.HerramientasPdf;

/// <summary>
/// Cuántas conversiones de Herramientas PDF decodifican o componen a la vez en todo el proceso. El presupuesto de memoria
/// (<see cref="Common.IPresupuestoMemoriaDocumentos"/>) acota bytes, no CPU: sin esto, varias sesiones con fotos que caben en
/// el presupuesto podían ocupar todos los núcleos y frenar el resto de pantallas de todos los Tenants. Es un singleton: un
/// solo semáforo para el proceso, no uno por circuito.
///
/// <para>
/// Cubre todo el trabajo de CPU de Foto a PDF: la miniatura de cada foto al añadirla y la composición del PDF al descargar.
/// La espera es cancelable (cerrar el circuito la corta) y está acotada por <see cref="EsperaMaxima"/>: quien no encuentra
/// hueco a tiempo no se queda en cola sin límite, recibe <c>null</c> y la pantalla avisa de que vuelva a intentarlo.
/// </para>
/// </summary>
public sealed class LimitadorConversionesPdf
{
    /// <summary>Lo que espera una conversión a que quede un hueco antes de rendirse.</summary>
    public static readonly TimeSpan EsperaMaximaPorDefecto = TimeSpan.FromSeconds(10);

    /// <summary>La mitad de los núcleos (al menos uno): deja la otra mitad para el resto de la aplicación.</summary>
    public static int HuecosPorDefecto => Math.Max(1, Environment.ProcessorCount / 2);

    private readonly SemaphoreSlim _huecos;

    public LimitadorConversionesPdf()
        : this(HuecosPorDefecto, EsperaMaximaPorDefecto)
    {
    }

    public LimitadorConversionesPdf(int huecos, TimeSpan esperaMaxima)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(huecos, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(esperaMaxima, TimeSpan.Zero);
        Huecos = huecos;
        EsperaMaxima = esperaMaxima;
        _huecos = new SemaphoreSlim(huecos, huecos);
    }

    public int Huecos { get; }

    public TimeSpan EsperaMaxima { get; }

    /// <summary>
    /// Espera un hueco como mucho <see cref="EsperaMaxima"/>. Devuelve el turno, que se suelta al desecharlo, o <c>null</c>
    /// si no hubo hueco a tiempo (no se ha tomado nada).
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> se canceló esperando.</exception>
    public async Task<IDisposable?> EntrarAsync(CancellationToken cancellationToken)
    {
        if (!await _huecos.WaitAsync(EsperaMaxima, cancellationToken))
            return null;

        // WaitAsync puede conceder el hueco aunque el token se cancelara a la vez que otro lo soltaba (la misma carrera que
        // documenta PuertaAccesoDatos): una conversión ya cancelada no se queda con él.
        if (cancellationToken.IsCancellationRequested)
        {
            _huecos.Release();
            cancellationToken.ThrowIfCancellationRequested();
        }

        return new Turno(_huecos);
    }

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
