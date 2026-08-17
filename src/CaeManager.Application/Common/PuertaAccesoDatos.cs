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
/// </summary>
public sealed class PuertaAccesoDatos : IDisposable
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
            LiberarSiSigueViva();
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
            LiberarSiSigueViva();
        }
    }

    /// <summary>
    /// El circuito de Blazor puede desconectarse (y con él, este scope y su
    /// puerta) mientras <c>operacion</c> todavía está en vuelo — reproducido
    /// en vivo con un deep-link en frío a Trabajador: la reconexión tira el
    /// scope antes de que la consulta en curso termine, y el <c>Release()</c>
    /// de este <c>finally</c> caía sobre un semáforo ya liberado por
    /// <see cref="Dispose"/>, lanzando <see cref="ObjectDisposedException"/>
    /// sin capturar — eso rompía el pipeline de MediatR y dejaba la página
    /// sin terminar de cargar nunca. No hay nada que liberar si el circuito
    /// ya se fue.
    /// </summary>
    private void LiberarSiSigueViva()
    {
        try
        {
            _puerta.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose() => _puerta.Dispose();
}
