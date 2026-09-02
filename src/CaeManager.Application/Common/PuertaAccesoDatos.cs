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
/// </summary>
public sealed class PuertaAccesoDatos
{
    private readonly SemaphoreSlim _puerta = new(1, 1);
    private readonly AsyncLocal<bool> _flujoDentro = new();

    public async Task<T> EjecutarAsync<T>(Func<Task<T>> operacion, CancellationToken cancellationToken = default)
    {
        if (_flujoDentro.Value)
            return await operacion();

        await _puerta.WaitAsync(cancellationToken);
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

        await _puerta.WaitAsync(cancellationToken);
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
