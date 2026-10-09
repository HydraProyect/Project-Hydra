namespace CaeManager.Web.Tests;

/// <summary>
/// Reloj de tests que solo avanza cuando se le dice. Sustituye al reloj de pared en los componentes que miden esperas
/// (<c>BotonConEspera</c>): un umbral de milisegundos contra el reloj real depende de lo cargada que esté la máquina, y la misma
/// aserción da verde o rojo según el turno. Aquí el tiempo es una variable del test. Dispara los temporizadores creados con
/// <see cref="CreateTimer"/> en el orden en que vencerían, de modo que avanzar 10 s con un periodo de 250 ms da 40 latidos.
/// <para>
/// Lo que NO hace: esperar a lo que un temporizador despierta. La continuación de un <c>await Task.Delay(…, reloj, …)</c> puede
/// correr en otro hilo y armar allí el temporizador siguiente (por eso la lista va con cerrojo), y ese nace después de
/// <see cref="Avanzar"/>: un solo avance no encadena varias esperas sucesivas. Quien avanza espera después al efecto que busca
/// —un estado, o <see cref="TemporizadoresVivos"/>— con el reloj parado, así que esa espera no compite con el tiempo del test.
/// </para>
/// </summary>
internal sealed class RelojManual : TimeProvider
{
    private readonly object _cerrojo = new();
    private readonly List<TemporizadorManual> _temporizadores = [];
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_cerrojo)
            return _ticks;
    }

    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_cerrojo)
        {
            var temporizador = new TemporizadorManual(this, callback, state, _ticks + dueTime.Ticks, period.Ticks);
            _temporizadores.Add(temporizador);
            return temporizador;
        }
    }

    /// <summary>Temporizadores vivos (no eliminados): para comprobar que el componente los suelta.</summary>
    public int TemporizadoresVivos
    {
        get
        {
            lock (_cerrojo)
                return _temporizadores.Count;
        }
    }

    public void Avanzar(TimeSpan cuanto)
    {
        long destino;
        lock (_cerrojo)
            destino = _ticks + cuanto.Ticks;

        while (true)
        {
            TemporizadorManual? siguiente;
            lock (_cerrojo)
            {
                siguiente = _temporizadores.Where(t => t.Vence <= destino).OrderBy(t => t.Vence).FirstOrDefault();
                if (siguiente is null)
                {
                    _ticks = destino;
                    return;
                }

                _ticks = Math.Max(_ticks, siguiente.Vence);
                siguiente.Reprogramar();
            }

            // Fuera del cerrojo: lo que despierta puede leer la hora, soltar este temporizador o armar otro.
            siguiente.Llamar();
        }
    }

    private void Quitar(TemporizadorManual t)
    {
        lock (_cerrojo)
            _temporizadores.Remove(t);
    }

    private sealed class TemporizadorManual(RelojManual reloj, TimerCallback callback, object? state, long vence, long periodo) : ITimer
    {
        public long Vence { get; private set; } = vence;

        /// <summary>Con el cerrojo del reloj tomado.</summary>
        public void Reprogramar() => Vence = periodo > 0 ? Vence + periodo : long.MaxValue;

        public void Llamar() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose() => reloj.Quitar(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
