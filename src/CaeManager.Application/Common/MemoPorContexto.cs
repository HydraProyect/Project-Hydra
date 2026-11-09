namespace CaeManager.Application.Common;

/// <summary>
/// Una memoización de lectura del layout que <see cref="InvalidacionAlcanceBehavior{TRequest,TResponse}"/> descarta
/// antes y después de cada Command. La composición registra cada <see cref="MemoPorContexto{TValor}"/> también con
/// esta interfaz, apuntando a la misma instancia Scoped.
/// </summary>
public interface IMemoPorContexto
{
    /// <summary>Descarta todas las entradas del ámbito.</summary>
    void Invalidar();
}

/// <summary>Constantes comunes a todos los <see cref="MemoPorContexto{TValor}"/>.</summary>
public static class MemoPorContexto
{
    /// <summary>Vida máxima de una entrada desde que empezó a calcularse.</summary>
    public static readonly TimeSpan Caducidad = TimeSpan.FromSeconds(10);

    /// <summary>
    /// <c>true</c> en el flujo asíncrono de un cálculo de cualquier memo (y en sus tareas hijas). Ahí una llamada
    /// nunca se une a un cálculo en vuelo: así ninguna espera entre memos puede cerrar un ciclo.
    /// </summary>
    internal static readonly AsyncLocal<bool> DentroDeUnCalculo = new();
}

/// <summary>
/// Memoización de una Query de lectura dentro de UN ámbito de DI: una petición HTTP (el prerenderizado y sus
/// middlewares) o un circuito de Blazor Server. El layout repite en cada visita las mismas Queries (menú lateral,
/// selector de Tenant beneficiario, migas, cabecera) en el prerenderizado y otra vez en el circuito.
///
/// <para>
/// <b>Aislamiento.</b> Cada subclase se registra <c>Scoped</c>, nunca singleton: un ámbito es de un único usuario
/// autenticado, y aun así la clave (<see cref="ClaveContextoMemo"/>) lleva todo lo que decide el resultado o el
/// contexto RLS con el que se leyó —usuario, Tenant de origen, perfil que decide el alcance, Tenant efectivo y
/// ámbito de Tenant explícito—, así que un cambio de cualquiera de ellos dentro del mismo circuito (otro Tenant
/// beneficiario activo, un fan-out con <c>AmbitoTenantExplicito</c>, una sesión con otra identidad) es otra entrada
/// y se resuelve de nuevo. Lo que no entra en la clave (lente de demo Gestor, vista previa de vocabulario) se
/// aplica fuera del memo, en cada llamada.
/// </para>
///
/// <para>
/// <b>Caducidad.</b> Un circuito vive horas, y una Asignación de Cartera cerrada desde otro circuito o que llega a
/// su <c>VigenciaHasta</c> no pasa por este. Cada entrada caduca a los <see cref="MemoPorContexto.Caducidad"/> segundos de
/// empezar a calcularse (reloj monotónico): cubre la visita, que es donde se repetía, y queda muy por debajo de la
/// cota de lectura del alcance memoizado (<c>CaducidadAlcanceOptions</c>, 60 s). Las escrituras no esperan:
/// <see cref="InvalidacionAlcanceBehavior{TRequest,TResponse}"/> llama a <see cref="Invalidar"/> antes y
/// después de cada Command, igual que con el alcance.
/// </para>
///
/// <para>
/// <b>Una sola lectura en vuelo por clave.</b> La entrada se guarda en cuanto empieza el cálculo, no cuando
/// termina: una llamada con la misma clave que llega mientras otra calcula espera esa misma lectura en vez de
/// lanzar la suya. Compartirla equivale a un acierto del memo, porque la clave es la misma y con ella el contexto.
/// El cálculo es de quien lo empieza: corre en su flujo asíncrono (dentro de su <see cref="PuertaAccesoDatos"/>)
/// y con su <see cref="CancellationToken"/>, y quien lo empieza lo espera hasta el final, así que nunca sigue
/// tocando el <c>DbContext</c> después de que su llamada suelte la puerta. Quien se une espera con su propio token:
/// si se cancela, solo se va él. Si el cálculo compartido falla, todos los que esperaban reciben el mismo error;
/// si lo cancela el token de quien lo empezó, quien se unió y no se canceló vuelve a intentarlo por su cuenta. Ni
/// un fallo ni una cancelación quedan guardados: la entrada se retira antes de completarse.
/// </para>
///
/// <para>
/// <b>Sin interbloqueo con la puerta.</b> Todo despacho de MediatR pasa por <see cref="PuertaAccesoDatos"/>
/// (<see cref="SerializacionAccesoDatosBehavior{TRequest,TResponse}"/>), así que quien empieza un cálculo ya está
/// dentro de ella y su cálculo no tiene que esperar a nadie para tomarla. Dentro de un cálculo de cualquier memo
/// (o de una tarea hija suya), una llamada no se une nunca a un cálculo en vuelo: usa un valor ya resuelto, empieza
/// uno si no hay entrada o, si la hay en vuelo, calcula aparte sin guardar. Así no hay ciclo de esperas posible:
/// solo espera a otro quien no está calculando nada, y a quien no calcula nada no lo espera nadie. Cubre la llamada
/// anidada con la misma clave (se esperaría a sí misma) y la de dos memos que se piden uno al otro desde flujos
/// hermanos.
/// </para>
///
/// Lo memoizado no autoriza nada por sí solo: los endpoints, los Commands y RLS siguen decidiendo con el dato
/// vigente.
/// </summary>
public abstract class MemoPorContexto<TValor>(TimeProvider? reloj = null) : IMemoPorContexto
{
    private readonly TimeProvider _reloj = reloj ?? TimeProvider.System;
    private readonly object _cerrojo = new();

    // Una entrada por clave, en vuelo o ya resuelta. Invalidar las retira todas: un cálculo que empezó antes de una
    // invalidación se devuelve a quien lo pidió (y a quien se unió antes de ella), pero ya no está en el diccionario,
    // así que nadie más lo recibe y no pisa la entrada nueva.
    private readonly Dictionary<ClaveContextoMemo, Entrada> _entradas = new();

    private sealed record Entrada(long Inicio, Task<TValor> Tarea);

    /// <summary>
    /// El valor memoizado para <paramref name="clave"/> si sigue vigente, el del cálculo en vuelo si otra llamada
    /// ya lo está calculando, o, si no hay ninguno, lo calcula con <paramref name="calcular"/> y lo guarda. El valor
    /// se comparte entre llamadas: lo que guarda es lo que devuelve <see cref="Congelar"/>.
    /// </summary>
    public async Task<TValor> ObtenerOCalcularAsync(
        ClaveContextoMemo clave,
        Func<CancellationToken, Task<TValor>> calcular,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Task<TValor>? compartida = null;
            TaskCompletionSource<TValor>? propia = null;
            Entrada? nueva = null;
            var aparte = false;
            lock (_cerrojo)
            {
                if (_entradas.TryGetValue(clave, out var entrada)
                    && _reloj.GetElapsedTime(entrada.Inicio) < MemoPorContexto.Caducidad)
                {
                    // Una entrada fallida o cancelada nunca está en el diccionario (se retira antes de completarse):
                    // resuelta, es un acierto. En vuelo, dentro de un cálculo no se espera (ver la clase).
                    if (entrada.Tarea.IsCompletedSuccessfully || !MemoPorContexto.DentroDeUnCalculo.Value)
                        compartida = entrada.Tarea;
                    else
                        aparte = true;
                }
                else
                {
                    // Sin crecer sin límite en un circuito largo: fuera lo caducado, de esta clave y de las demás.
                    foreach (var caducada in _entradas
                                 .Where(e => _reloj.GetElapsedTime(e.Value.Inicio) >= MemoPorContexto.Caducidad)
                                 .Select(e => e.Key).ToList())
                        _entradas.Remove(caducada);

                    propia = new TaskCompletionSource<TValor>(TaskCreationOptions.RunContinuationsAsynchronously);
                    nueva = new Entrada(_reloj.GetTimestamp(), propia.Task);
                    _entradas[clave] = nueva;
                }
            }

            if (aparte)
                return Congelar(await calcular(cancellationToken));

            if (propia is not null)
                return await CalcularYPublicarAsync(clave, nueva!, propia, calcular, cancellationToken);

            try
            {
                // Resuelta, WaitAsync la devuelve tal cual (un acierto no mira el token, como antes).
                return await compartida!.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Lo canceló el token de quien lo empezó, no el nuestro: la entrada ya se retiró; se vuelve a
                // intentar (uniéndose a otro cálculo en vuelo o empezando uno).
            }
        }
    }

    private async Task<TValor> CalcularYPublicarAsync(
        ClaveContextoMemo clave, Entrada entrada, TaskCompletionSource<TValor> publicacion,
        Func<CancellationToken, Task<TValor>> calcular, CancellationToken cancellationToken)
    {
        // El cambio de un AsyncLocal dentro de un método async solo lo ven el propio método y lo que llama; al
        // volver, quien llamó conserva su valor.
        MemoPorContexto.DentroDeUnCalculo.Value = true;

        TValor valor;
        try
        {
            valor = Congelar(await calcular(cancellationToken));
        }
        catch (Exception error) when (error is OperationCanceledException || cancellationToken.IsCancellationRequested)
        {
            // Con el token de quien lo empezó ya cancelado, el proveedor puede expresar la cancelación con otra
            // excepción (una de E/S, un objeto ya dispuesto): para quien se unió sigue siendo una cancelación
            // ajena y reintenta.
            Retirar(clave, entrada);
            publicacion.TrySetCanceled();
            throw;
        }
        catch (Exception error)
        {
            Retirar(clave, entrada);
            publicacion.TrySetException(error);
            // Leer Exception la marca observada: sin nadie unido, no saldría como UnobservedTaskException.
            _ = publicacion.Task.Exception;
            throw;
        }

        publicacion.TrySetResult(valor);
        return valor;
    }

    /// <summary>Retira la entrada si sigue siendo la de este cálculo (no la de uno posterior a un Invalidar).</summary>
    private void Retirar(ClaveContextoMemo clave, Entrada entrada)
    {
        lock (_cerrojo)
        {
            if (_entradas.TryGetValue(clave, out var actual) && ReferenceEquals(actual, entrada))
                _entradas.Remove(clave);
        }
    }

    /// <summary>
    /// Copia inmutable del valor calculado, la que se guarda y se comparte. Por defecto el propio valor: basta
    /// para tipos de valor e inmutables; una colección la sobrescribe.
    /// </summary>
    protected virtual TValor Congelar(TValor valor) => valor;

    /// <summary>Descarta todas las entradas del ámbito. Lo llama <c>InvalidacionAlcanceBehavior</c> en cada Command.</summary>
    public void Invalidar()
    {
        lock (_cerrojo)
            _entradas.Clear();
    }
}

/// <summary>
/// Todo lo que decide el resultado de una Query memoizada con <see cref="MemoPorContexto{TValor}"/> o el contexto
/// RLS con el que se lee (<c>app.tenant_id</c>, <c>app.tenant_origen_id</c>, <c>app.usuario_id</c>): dos llamadas
/// con la misma clave leen lo mismo.
/// </summary>
/// <param name="UsuarioId">Usuario autenticado (claim), el de las carteras y delegaciones.</param>
/// <param name="TenantOrigenId">Tenant de origen del usuario (claim).</param>
/// <param name="PerfilQueDecideAlcance">Perfil de origen que decide qué Asignaciones de Operación alcanzan.</param>
/// <param name="TenantEfectivo">Tenant efectivo (<c>ITenantActual</c>): el Tenant beneficiario o Empresa gestionada activa.</param>
/// <param name="AmbitoTenantExplicito">Tenant fijado por <c>AmbitoTenantExplicito</c> en un fan-out, si lo hay.</param>
public readonly record struct ClaveContextoMemo(
    Guid UsuarioId,
    Guid TenantOrigenId,
    string? PerfilQueDecideAlcance,
    Guid? TenantEfectivo,
    Guid? AmbitoTenantExplicito)
{
    /// <summary>
    /// La clave del contexto actual, o <c>null</c> sin usuario o sin Tenant de origen: entonces no se memoiza.
    /// Todo sale de claims y del <c>AsyncLocal</c>, sin leer la base.
    /// </summary>
    public static async Task<ClaveContextoMemo?> ResolverAsync(ICurrentUserService currentUser, ITenantActual tenantActual)
    {
        var usuarioId = await currentUser.ObtenerUsuarioActualIdAsync();
        var tenantOrigenId = await currentUser.ObtenerTenantOrigenIdAsync();
        if (usuarioId is null || tenantOrigenId is null) return null;

        return new ClaveContextoMemo(
            usuarioId.Value, tenantOrigenId.Value, await currentUser.ObtenerPerfilQueDecideAlcanceAsync(),
            tenantActual.TenantId, Common.AmbitoTenantExplicito.TenantIdActual);
    }
}
