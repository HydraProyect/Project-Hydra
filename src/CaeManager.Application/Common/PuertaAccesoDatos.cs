namespace CaeManager.Application.Common;

/// <summary>
/// Serializa el acceso a datos dentro de un mismo scope de DI.
///
/// Existe por Blazor Server + PostgreSQL: durante el renderizado, varios
/// componentes (layout y página) se inicializan en paralelo compartiendo el
/// mismo <c>CaeManagerDbContext</c> scoped, y un DbContext no admite dos
/// operaciones en vuelo. Sobre SQLite la carrera nunca afloró porque sus
/// operaciones "async" completan de forma síncrona; Npgsql hace I/O asíncrona
/// real y la destapa ("A second operation was started on this context
/// instance", reproducido en el primer arranque contra PostgreSQL).
///
/// Todo camino que llegue al DbContext desde un componente tiene que pasar por
/// aquí: los despachos de MediatR entran solos
/// (<see cref="SerializacionAccesoDatosBehavior{TRequest,TResponse}"/>); los
/// accesos directos (UserManager en páginas y layout,
/// <c>DirectorioUsuariosTenant</c>, <c>TrazaSoporteService</c>) se envuelven
/// en su sitio.
///
/// Reentrante por flujo asíncrono: quien ya tiene la puerta vuelve a entrar
/// sin bloquearse (un handler que despacha otro request de MediatR, ver
/// <c>ObtenerKpisGlobalesQuery</c>, o una página que envuelve su carga entera
/// y dentro despacha Queries).
///
/// Scoped: serializa dentro de una petición HTTP o de un circuito de Blazor,
/// nunca entre usuarios distintos.
///
/// <para>
/// <b>Deliberadamente NO implementa <see cref="IDisposable"/>.</b> Versiones
/// anteriores sí llamaban <c>_puerta.Dispose()</c> cuando el scope de DI
/// terminaba, y eso fue la causa raíz de tres incidentes reales en producción
/// (Sentry DOTNET-2, DOTNET-5, DOTNET-6): el circuito de Blazor puede
/// desconectarse —y con él, el scope— mientras otro componente todavía tiene
/// una operación en vuelo esperando esta puerta o a punto de liberarla.
/// <c>SemaphoreSlim.Dispose()</c> concurrente con <c>WaitAsync</c>/<c>Release</c>
/// no es un uso soportado (la documentación de la BCL exige que Dispose solo
/// se llame cuando el resto de operaciones ya terminaron) y, comprobado en
/// aislamiento, el resultado no es un simple <see cref="ObjectDisposedException"/>
/// predecible: según el orden exacto de la carrera, una espera pendiente
/// puede quedarse colgada para siempre, y ni siquiera cancelarla con un
/// <see cref="CancellationToken"/> la rescata de forma fiable una vez que
/// Dispose ya corrió (probado con un timeout compuesto por fuera del
/// semáforo Y con cancelación nativa vía <c>WaitAsync(CancellationToken)</c>
/// — ambas variantes se colgaron igual bajo esa carrera concreta).
/// </para>
/// <para>
/// La salida correcta no es coordinar mejor la carrera: es no tenerla.
/// <see cref="SemaphoreSlim"/> no retiene ningún recurso no administrado
/// salvo que se acceda a <c>AvailableWaitHandle</c> (un
/// <c>ManualResetEvent</c> nativo creado de forma perezosa) — algo que esta
/// clase nunca hace. Sin esa propiedad, no disponer el semáforo no filtra
/// nada: cuando el scope termina y nadie más referencia esta instancia, el
/// recolector de basura se encarga, igual que con cualquier otro servicio
/// scoped que no implementa <see cref="IDisposable"/>. Una operación todavía
/// en vuelo cuando el circuito se va simplemente termina con normalidad
/// —adquiere, ejecuta, libera— aunque ya no quede nadie esperando el
/// resultado; la que escucha <see cref="AlCerrarse"/> recibe además la
/// petición de terminar cuanto antes.
/// </para>
/// <para>
/// <b>Lo que sí hay que esperar es a la operación, no al semáforo.</b> Que una
/// operación en vuelo "termine con normalidad" es cierto para el semáforo pero
/// falso para lo que la operación toca: cuando el circuito se cierra, el
/// framework dispone el scope de DI y con él el <c>CaeManagerDbContext</c> y su
/// conexión, mientras la consulta sigue leyendo de ella desde otro hilo. Medido
/// contra PostgreSQL real, disponer el contexto con una consulta en vuelo
/// produce <c>ArgumentOutOfRangeException</c> en
/// <c>NpgsqlDataReader.ProcessMessage</c> (la lectura y el cierre pisan el
/// mismo protocolo), <c>ObjectDisposedException</c>, cuelgues del propio
/// <c>Dispose</c> y —lo peor— conexiones devueltas al pool con el protocolo
/// desincronizado, que rompen la primera consulta de OTRO circuito que las
/// reciba. Por eso el cierre del circuito llama a <see cref="CerrarAsync"/>
/// antes de que el scope se disponga (ver
/// <c>LiberacionDeAccesoADatosAlCerrarCircuito</c>): la puerta deja de admitir
/// operaciones nuevas y espera, con tope, a que la que está en vuelo termine.
/// Sigue sin disponerse el semáforo.
/// </para>
/// <para>
/// <b>Una conexión por operación.</b> La puerta delimita la operación: desde que
/// se toma hasta que se suelta, nadie más en el scope toca el <c>DbContext</c>.
/// Por eso es también el sitio donde la capa de datos puede retener la conexión
/// física abierta durante la operación entera, en vez de abrirla y cerrarla en
/// cada comando y pagar en cada apertura el contexto RLS firmado
/// (<c>TenantRlsConnectionInterceptor</c>). La puerta no sabe nada de EF: ofrece
/// <see cref="RetenerHastaSalir"/>, que solo concede la retención desde el flujo
/// de la operación que tiene la puerta en ese instante, y libera lo retenido
/// <b>antes</b> de soltar el semáforo, también si la operación lanzó. Así
/// <see cref="CerrarAsync"/>, que espera al semáforo, solo vuelve con la conexión
/// ya devuelta al pool, y la operación siguiente del scope nunca hereda la
/// conexión de la anterior.
/// </para>
/// </summary>
public sealed class PuertaAccesoDatos
{
    private readonly SemaphoreSlim _puerta = new(1, 1);
    private readonly AsyncLocal<OperacionEnCurso?> _flujoDentro = new();
    private OperacionEnCurso? _operacionEnCurso;
    private volatile bool _cerrada;

    // Sin Dispose, por lo mismo que el semáforo: sin temporizador ni WaitHandle
    // (nunca se lee Token.WaitHandle), no retiene nada no administrado.
    private readonly CancellationTokenSource _cierre = new();

    // Solo medición (Server-Timing «puerta»): cuánto se espera en la cola del
    // semáforo. No cambia cuándo se entra ni quién entra.
    private readonly CosteCarga? _coste;

    public PuertaAccesoDatos()
    {
    }

    public PuertaAccesoDatos(CosteCarga coste) => _coste = coste;

    /// <summary>
    /// <c>true</c> cuando el scope ya no admite operaciones nuevas (ver
    /// <see cref="CerrarAsync"/>).
    /// </summary>
    public bool Cerrada => _cerrada;

    /// <summary>
    /// Se cancela al empezar <see cref="CerrarAsync"/>, antes de esperar a la
    /// operación en vuelo. Es para una operación larga que no debe atarse a la
    /// vida de un componente (salir de la pantalla no la corta a medias) pero sí
    /// terminar cuanto antes cuando el circuito se va: así acaba dentro del tope
    /// de la espera, en vez de seguir después con el <c>DbContext</c> ya
    /// dispuesto. Nadie más lo usa: una operación que no lo reciba termina como
    /// siempre.
    /// </summary>
    public CancellationToken AlCerrarse => _cierre.Token;

    /// <summary>
    /// Cierra la puerta y espera a que la operación en vuelo, si la hay, termine.
    /// Se llama cuando el circuito se cierra, <b>antes</b> de que el scope de DI
    /// (y con él el <c>DbContext</c> y su conexión) se disponga: ver el
    /// comentario de la clase para lo que ocurre si se dispone con una consulta
    /// en vuelo.
    ///
    /// <para>
    /// Desde la llamada, toda operación nueva —también las que ya estaban en la
    /// cola del semáforo— termina con <see cref="OperationCanceledException"/>
    /// sin ejecutarse; es la misma excepción con la que <c>LoggingBehavior</c>
    /// ya reconoce "el circuito se fue" y no lo cuenta como fallo. Una operación
    /// que ya tenía la puerta —y las anidadas de su mismo flujo— sí termina; si
    /// escucha <see cref="AlCerrarse"/>, se le pide que lo haga cuanto antes, y
    /// se le pide antes de empezar a esperarla.
    /// </para>
    ///
    /// <para>
    /// La espera está acotada: no se puede esperar indefinidamente a una
    /// consulta colgada, y el tope evita retener el cierre del circuito.
    /// Devuelve <c>false</c> si venció con la operación todavía en vuelo (el
    /// scope se dispondrá igualmente: el mismo riesgo de siempre, pero solo tras
    /// el tope y no en cada cierre). Idempotente.
    /// </para>
    /// </summary>
    public async Task<bool> CerrarAsync(TimeSpan esperaMaxima)
    {
        _cerrada = true;
        try
        {
            _cierre.Cancel();
        }
        catch (AggregateException)
        {
            // Un callback de quien escuchaba AlCerrarse lanzó: no puede impedir el cierre.
        }

        if (!await _puerta.WaitAsync(esperaMaxima))
            return false;

        // Quien la tuviera ya la soltó. Se libera para que las operaciones que
        // esperaban en el semáforo despierten y vean la puerta cerrada (ver
        // AdquirirAsync) en vez de quedarse esperando para siempre.
        _puerta.Release();
        return true;
    }

    /// <summary>
    /// Toma la puerta. Si está cerrada —desde antes o porque se cerró mientras
    /// se esperaba—, no la toma y cancela: nadie debe tocar un
    /// <c>DbContext</c> que está a punto de disponerse.
    /// </summary>
    private async Task AdquirirAsync(CancellationToken cancellationToken)
    {
        if (_cerrada)
            throw new OperationCanceledException(MensajePuertaCerrada);

        var inicioEspera = System.Diagnostics.Stopwatch.GetTimestamp();
        await _puerta.WaitAsync(cancellationToken);
        _coste?.RegistrarEsperaPuerta(System.Diagnostics.Stopwatch.GetElapsedTime(inicioEspera));
        // SemaphoreSlim.WaitAsync puede conceder el semáforo aunque el token ya
        // estuviera cancelado en el momento del Release() de quien lo tenía: es
        // una carrera de la propia BCL entre "cancelar" y "liberar", no algo que
        // WaitAsync garantice resolver a favor de la cancelación. Sin este
        // segundo control, un circuito de Blazor ya retirado podría ejecutar
        // igualmente la operación que se creía cancelada.
        if (cancellationToken.IsCancellationRequested || _cerrada)
        {
            _puerta.Release();
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException(MensajePuertaCerrada);
        }
    }

    private const string MensajePuertaCerrada =
        "La puerta de acceso a datos está cerrada: el circuito se cerró y su DbContext se va a disponer.";

    public async Task<T> EjecutarAsync<T>(Func<Task<T>> operacion, CancellationToken cancellationToken = default)
    {
        if (EsReentrada())
            return await operacion();

        await AdquirirAsync(cancellationToken);
        var enCurso = Entrar();
        try
        {
            return await operacion();
        }
        finally
        {
            await SalirAsync(enCurso);
        }
    }

    public async Task EjecutarAsync(Func<Task> operacion, CancellationToken cancellationToken = default)
    {
        if (EsReentrada())
        {
            await operacion();
            return;
        }

        await AdquirirAsync(cancellationToken);
        var enCurso = Entrar();
        try
        {
            await operacion();
        }
        finally
        {
            await SalirAsync(enCurso);
        }
    }

    /// <summary>
    /// Retiene <paramref name="recurso"/> hasta que la operación que tiene la
    /// puerta termine: llama a <paramref name="adquirir"/> ahora y a
    /// <paramref name="liberar"/> al salir, antes de soltar el semáforo, haya
    /// terminado bien o con excepción. Devuelve <c>false</c> sin llamar a nada si
    /// el flujo que lo pide no es el de la operación en curso (no hay operación,
    /// ya terminó, o es un flujo rezagado de una anterior) o si ese recurso ya
    /// está retenido en esta operación. Si <paramref name="adquirir"/> lanza, no
    /// queda nada retenido.
    ///
    /// <para>
    /// Para la capa de datos (<c>TenantRlsConnectionInterceptor</c>), que retiene
    /// así la conexión física durante la operación. <paramref name="liberar"/> no
    /// debería lanzar: es limpieza, y se ejecuta sin token de cancelación.
    /// </para>
    /// </summary>
    public bool RetenerHastaSalir(object recurso, Action adquirir, Func<ValueTask> liberar)
    {
        ArgumentNullException.ThrowIfNull(recurso);
        ArgumentNullException.ThrowIfNull(adquirir);
        ArgumentNullException.ThrowIfNull(liberar);

        var enCurso = _flujoDentro.Value;
        if (enCurso is null || !ReferenceEquals(enCurso, Volatile.Read(ref _operacionEnCurso)))
            return false;

        return enCurso.Retener(recurso, adquirir, liberar);
    }

    /// <summary>
    /// Reentrada: el flujo pertenece a la operación que tiene la puerta ahora
    /// (un <c>Send</c> anidado, o una tarea hija que la operación aún espera).
    /// Un flujo rezagado de una operación ya terminada hereda el
    /// <c>AsyncLocal</c> pero no es reentrada: entra por el semáforo como una
    /// operación nueva, porque si no correría a la vez que la operación que sí
    /// tiene la puerta, sobre el mismo <c>DbContext</c> y la conexión que esa
    /// operación retiene, o después de <see cref="CerrarAsync"/>.
    /// </summary>
    private bool EsReentrada() =>
        _flujoDentro.Value is { } enCurso && ReferenceEquals(enCurso, Volatile.Read(ref _operacionEnCurso));
    private OperacionEnCurso Entrar()
    {
        var enCurso = new OperacionEnCurso();
        Volatile.Write(ref _operacionEnCurso, enCurso);
        _flujoDentro.Value = enCurso;
        return enCurso;
    }

    /// <summary>
    /// Libera lo retenido y después suelta el semáforo. El <c>finally</c>
    /// interior garantiza que el semáforo se suelta aunque liberar lanzara: una
    /// puerta que no se suelta deja el scope entero bloqueado.
    ///
    /// <para>
    /// La operación deja de ser la vigente <b>antes</b> de liberar (segunda
    /// revisión de Codex): mientras se cierra la conexión o se restaura el
    /// sellado, un flujo rezagado de esta operación que despierte no es
    /// reentrada, espera al semáforo como operación nueva y no usa el
    /// <c>DbContext</c> a la vez que la limpieza. Las liberaciones no pasan por
    /// <see cref="EjecutarAsync(Func{Task}, CancellationToken)"/>, así que no se
    /// esperan a sí mismas.
    /// </para>
    /// </summary>
    private async Task SalirAsync(OperacionEnCurso enCurso)
    {
        Volatile.Write(ref _operacionEnCurso, null);
        try
        {
            await enCurso.TerminarAsync();
        }
        finally
        {
            _flujoDentro.Value = null;
            _puerta.Release();
        }
    }

    /// <summary>
    /// Una operación de la puerta: identifica el flujo que la tiene (los
    /// anidados lo heredan por el <c>AsyncLocal</c>) y guarda lo que retiene.
    /// </summary>
    private sealed class OperacionEnCurso
    {
        private readonly object _candado = new();
        private readonly List<(object Recurso, Func<ValueTask> Liberar)> _retenidos = [];
        private bool _terminada;

        public bool Retener(object recurso, Action adquirir, Func<ValueTask> liberar)
        {
            lock (_candado)
            {
                if (_terminada || _retenidos.Exists(r => ReferenceEquals(r.Recurso, recurso)))
                    return false;

                adquirir();
                _retenidos.Add((recurso, liberar));
                return true;
            }
        }

        /// <summary>
        /// Marca la operación como terminada —ningún flujo puede retener nada
        /// más en ella— y libera en orden inverso. Si una liberación lanza, se
        /// intentan las demás y se relanza la primera excepción al final.
        /// </summary>
        public async Task TerminarAsync()
        {
            List<(object Recurso, Func<ValueTask> Liberar)> retenidos;
            lock (_candado)
            {
                _terminada = true;
                retenidos = [.. _retenidos];
                _retenidos.Clear();
            }

            Exception? primerError = null;
            for (var i = retenidos.Count - 1; i >= 0; i--)
            {
                try
                {
                    await retenidos[i].Liberar();
                }
                catch (Exception ex)
                {
                    primerError ??= ex;
                }
            }

            if (primerError is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(primerError);
        }
    }
}
