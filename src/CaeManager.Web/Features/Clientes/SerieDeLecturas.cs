namespace CaeManager.Web.Features.Clientes;

/// <summary>
/// Turno de lectura compartido por Cliente 360 y sus componentes con lecturas propias (fila «Gestor CAE», pestaña
/// «Documentación») y por las lecturas de pestaña de la página: todas usan el DbContext del circuito, que no admite
/// dos operaciones a la vez. Quien lee toma el turno y lo suelta al terminar, también si su lectura se canceló, así que
/// la siguiente no empieza hasta que la anterior ha salido del DbContext. Sin Dispose a propósito: el semáforo no usa
/// su WaitHandle, y así un componente que suelta el turno después de que la página se desmonte no falla.
/// </summary>
public sealed class SerieDeLecturas
{
    private readonly SemaphoreSlim _turno = new(1, 1);

    /// <summary>Espera el turno; se suelta con el <see cref="IDisposable"/> devuelto.</summary>
    public async Task<IDisposable> TomarAsync(CancellationToken cancellationToken)
    {
        await _turno.WaitAsync(cancellationToken);
        return new Turno(_turno);
    }

    /// <summary>Para quien recibe la serie como parámetro opcional: sin serie no hay nada que esperar.</summary>
    public static Task<IDisposable> TomarSiHayAsync(SerieDeLecturas? serie, CancellationToken cancellationToken) =>
        serie is null ? Task.FromResult<IDisposable>(SinTurno.Instancia) : serie.TomarAsync(cancellationToken);

    private sealed class Turno(SemaphoreSlim turno) : IDisposable
    {
        private int _soltado;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _soltado, 1) == 0)
                turno.Release();
        }
    }

    private sealed class SinTurno : IDisposable
    {
        public static readonly SinTurno Instancia = new();

        public void Dispose()
        {
        }
    }
}
