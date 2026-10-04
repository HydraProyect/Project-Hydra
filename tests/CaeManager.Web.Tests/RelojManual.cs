namespace CaeManager.Web.Tests;

/// <summary>
/// Reloj de tests que solo avanza cuando se le dice. Sustituye al reloj de pared en los componentes que miden esperas
/// (<c>BotonConEspera</c>): un umbral de milisegundos contra el reloj real depende de lo cargada que esté la máquina, y la misma
/// aserción da verde o rojo según el turno. Aquí el tiempo es una variable del test. Dispara los temporizadores creados con
/// <see cref="CreateTimer"/> en el orden en que vencerían, de modo que avanzar 10 s con un periodo de 250 ms da 40 latidos.
/// </summary>
internal sealed class RelojManual : TimeProvider
{
    private readonly List<TemporizadorManual> _temporizadores = [];
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero).AddTicks(_ticks);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var temporizador = new TemporizadorManual(this, callback, state, _ticks + dueTime.Ticks, period.Ticks);
        _temporizadores.Add(temporizador);
        return temporizador;
    }

    /// <summary>Temporizadores vivos (no eliminados): para comprobar que el componente los suelta.</summary>
    public int TemporizadoresVivos => _temporizadores.Count;

    public void Avanzar(TimeSpan cuanto)
    {
        var destino = _ticks + cuanto.Ticks;
        while (true)
        {
            var siguiente = _temporizadores.Where(t => t.Vence <= destino).OrderBy(t => t.Vence).FirstOrDefault();
            if (siguiente is null) break;

            _ticks = Math.Max(_ticks, siguiente.Vence);
            siguiente.Disparar();
        }

        _ticks = destino;
    }

    private void Quitar(TemporizadorManual t) => _temporizadores.Remove(t);

    private sealed class TemporizadorManual(RelojManual reloj, TimerCallback callback, object? state, long vence, long periodo) : ITimer
    {
        public long Vence { get; private set; } = vence;

        public void Disparar()
        {
            Vence = periodo > 0 ? Vence + periodo : long.MaxValue;
            callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose() => reloj.Quitar(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
