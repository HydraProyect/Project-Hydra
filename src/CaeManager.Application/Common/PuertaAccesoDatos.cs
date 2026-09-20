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
/// resultado.
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
/// </summary>
public sealed class PuertaAccesoDatos
{
    private readonly SemaphoreSlim _puerta = new(1, 1);
    private readonly AsyncLocal<bool> _flujoDentro = new();
    private volatile bool _cerrada;

    /// <summary>
    /// <c>true</c> cuando el scope ya no admite operaciones nuevas (ver
    /// <see cref="CerrarAsync"/>).
    /// </summary>
    public bool Cerrada => _cerrada;

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
    /// que ya tenía la puerta —y las anidadas de su mismo flujo— sí termina.
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

        await _puerta.WaitAsync(cancellationToken);
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
        if (_flujoDentro.Value)
            return await operacion();

        await AdquirirAsync(cancellationToken);
        _flujoDentro.Value = true;
        try
        {
            return await operacion();
        }
        finally
        {
            _flujoDentro.Value = false;
            _puerta.Release();
        }
    }

    public async Task EjecutarAsync(Func<Task> operacion, CancellationToken cancellationToken = default)
    {
        if (_flujoDentro.Value)
        {
            await operacion();
            return;
        }

        await AdquirirAsync(cancellationToken);
        _flujoDentro.Value = true;
        try
        {
            await operacion();
        }
        finally
        {
            _flujoDentro.Value = false;
            _puerta.Release();
        }
    }
}
