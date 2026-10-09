namespace CaeManager.Web.Tests;

/// <summary>
/// Reloj de prueba que además gobierna los temporizadores: el tiempo solo pasa cuando la prueba
/// llama a <see cref="Avanzar"/>, y lo que venza por el camino (un <c>Task.Delay(…, reloj, …)</c>)
/// se dispara en orden, cada cosa con el reloj puesto en su propio vencimiento.
/// <para>
/// Lo que NO hace: esperar a que termine lo que un temporizador despierta. La continuación de un
/// <c>await</c> puede ejecutarse en otro hilo, así que quien avanza el reloj espera después al
/// efecto que busca (un estado, o <see cref="Pendientes"/> cuando el código arma el tramo
/// siguiente). Esa espera no compite con el tiempo de la prueba: el reloj está parado.
/// </para>
/// </summary>
internal sealed class RelojConTemporizadores : TimeProvider
{
    private readonly object _cerrojo = new();
    private readonly List<Temporizador> _temporizadores = [];
    private DateTimeOffset _ahora = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);

    /// <summary>Temporizadores armados que todavía no han vencido ni se han cancelado.</summary>
    public int Pendientes
    {
        get
        {
            lock (_cerrojo)
                return _temporizadores.Count;
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_cerrojo)
            return _ahora;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var temporizador = new Temporizador(this, callback, state);
        temporizador.Change(dueTime, period);
        return temporizador;
    }

    public void Avanzar(TimeSpan cuanto)
    {
        DateTimeOffset destino;
        lock (_cerrojo)
            destino = _ahora + cuanto;

        while (true)
        {
            Temporizador? vencido;
            lock (_cerrojo)
            {
                vencido = _temporizadores.Where(t => t.Vence <= destino).MinBy(t => t.Vence);
                if (vencido is null)
                {
                    _ahora = destino;
                    return;
                }

                _temporizadores.Remove(vencido);
                if (vencido.Vence > _ahora)
                    _ahora = vencido.Vence;
            }

            // Fuera del cerrojo: lo que despierta puede armar otro temporizador o leer la hora.
            vencido.Disparar();
        }
    }

    /// <summary>De un solo disparo: el periodo se ignora, que es lo que usa <c>Task.Delay</c>.</summary>
    private sealed class Temporizador(RelojConTemporizadores reloj, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset Vence { get; private set; }

        public void Disparar() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (reloj._cerrojo)
            {
                reloj._temporizadores.Remove(this);
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    Vence = reloj._ahora + dueTime;
                    reloj._temporizadores.Add(this);
                }
            }

            return true;
        }

        public void Dispose()
        {
            lock (reloj._cerrojo)
                reloj._temporizadores.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
