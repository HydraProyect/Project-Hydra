using CaeManager.Application.EstadoMaterializado.Frescura;

namespace CaeManager.Web.Components;

/// <summary>
/// Repinta una pantalla cuando cambia el estado materializado de un Tenant que su circuito ha leído (diseño del estado
/// materializado, § 13.11): escucha <see cref="CacheEstadoCircuito.CambioRecibido"/> y, en el despachador del componente
/// (<c>InvokeAsync</c>), vuelve a pedir sus datos y llama a <c>StateHasChanged</c>.
///
/// <para>
/// El aviso no trae datos: la relectura pasa por la consulta de siempre, con el contexto RLS y el alcance del usuario del
/// circuito, así que un aviso nunca enseña nada fuera de su alcance. La caché solo entrega avisos de los Tenants cuya versión
/// leyó este circuito.
/// </para>
///
/// <para>
/// Varios avisos seguidos no hacen varias relecturas a la vez: si llega uno mientras se relee, se relee una vez más al acabar.
/// Un fallo de la relectura se registra y la pantalla se queda con lo que ya enseñaba; la siguiente lectura lo corrige.
/// </para>
/// </summary>
public sealed class RepintadoPorCambioDeEstado : IDisposable
{
    private readonly CacheEstadoCircuito _cache;
    private readonly Func<Func<Task>, Task> _enDespachador;
    private readonly Func<Task> _recargar;
    private readonly Action<Exception> _registrarFallo;
    private bool _recargando;
    private bool _pendiente;
    private volatile bool _liberado;

    private RepintadoPorCambioDeEstado(
        CacheEstadoCircuito cache, Func<Func<Task>, Task> enDespachador, Func<Task> recargar, Action<Exception> registrarFallo)
    {
        _cache = cache;
        _enDespachador = enDespachador;
        _recargar = recargar;
        _registrarFallo = registrarFallo;
        _cache.CambioRecibido += AlCambiar;
    }

    /// <summary>
    /// Empieza a escuchar. Sin caché en el ámbito (un test que no la registra, un ámbito sin circuito) no escucha nada y
    /// devuelve <c>null</c>.
    /// </summary>
    /// <param name="enDespachador">El <c>InvokeAsync</c> del componente.</param>
    /// <param name="recargar">Vuelve a pedir los datos y repinta (<c>StateHasChanged</c>); corre en el despachador.</param>
    public static RepintadoPorCambioDeEstado? Escuchar(
        CacheEstadoCircuito? cache, Func<Func<Task>, Task> enDespachador, Func<Task> recargar, Action<Exception> registrarFallo)
    {
        ArgumentNullException.ThrowIfNull(enDespachador);
        ArgumentNullException.ThrowIfNull(recargar);
        ArgumentNullException.ThrowIfNull(registrarFallo);
        return cache is null ? null : new RepintadoPorCambioDeEstado(cache, enDespachador, recargar, registrarFallo);
    }

    public void Dispose()
    {
        if (_liberado)
            return;
        _liberado = true;
        _cache.CambioRecibido -= AlCambiar;
    }

    // Corre en el hilo de quien publicó el aviso (tras el COMMIT de otra petición o de otro circuito): solo salta al
    // despachador del componente, sin esperar, para no retener a quien publicó.
    private void AlCambiar(AvisoCambioEstado aviso)
    {
        if (_liberado)
            return;
        _ = SaltarAlDespachadorAsync();
    }

    private async Task SaltarAlDespachadorAsync()
    {
        try
        {
            await _enDespachador(RecargarAsync);
        }
        catch (Exception excepcion) when (excepcion is ObjectDisposedException or OperationCanceledException)
        {
            // El circuito se cerró entre el aviso y el salto: no hay nada que repintar.
        }
        catch (Exception excepcion)
        {
            _registrarFallo(excepcion);
        }
    }

    // En el despachador: un solo hilo lógico, así que las dos marcas no necesitan cerrojo.
    private async Task RecargarAsync()
    {
        if (_liberado)
            return;
        if (_recargando)
        {
            _pendiente = true;
            return;
        }

        _recargando = true;
        try
        {
            do
            {
                _pendiente = false;
                await _recargar();
            }
            while (_pendiente && !_liberado);
        }
        catch (OperationCanceledException)
        {
            // La pantalla se retiró mientras releía.
        }
        catch (Exception excepcion)
        {
            _registrarFallo(excepcion);
        }
        finally
        {
            _recargando = false;
        }
    }
}
