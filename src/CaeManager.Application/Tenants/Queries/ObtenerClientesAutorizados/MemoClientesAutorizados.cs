namespace CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;

/// <summary>
/// Memoización de <see cref="ObtenerClientesAutorizadosQuery"/> dentro de UN ámbito de DI: una petición HTTP
/// (el prerenderizado y sus middlewares) o un circuito de Blazor Server. Una sola visita a una pantalla la pedía
/// cinco veces (NavMenu y SelectorClienteActivo en el prerenderizado y en el circuito, más
/// <c>TenantBeneficiarioPorDefectoMiddleware</c>), cada una con sus cinco lecturas.
///
/// <para>
/// <b>Aislamiento.</b> Es <c>Scoped</c>, nunca singleton: un ámbito es de un único usuario autenticado, y aun así la
/// clave (<see cref="ClaveMemoClientesAutorizados"/>) lleva todo lo que decide el resultado o el contexto RLS con el
/// que se leyó —usuario, Tenant de origen, perfil que decide el alcance, Tenant efectivo y ámbito de Tenant
/// explícito—, así que un cambio de cualquiera de ellos dentro del mismo circuito (otro Tenant beneficiario
/// activo, un fan-out con <c>AmbitoTenantExplicito</c>, una sesión con otra identidad) es otra entrada y se
/// resuelve de nuevo. La lente de demo Gestor no entra en la clave porque se aplica DESPUÉS, en cada llamada,
/// sobre una copia: lo memoizado es siempre la lista real.
/// </para>
///
/// <para>
/// <b>Caducidad.</b> Un circuito vive horas, y una Asignación de Cartera cerrada desde otro circuito o que llega a
/// su <c>VigenciaHasta</c> no pasa por este. Cada entrada caduca a los <see cref="Caducidad"/> segundos de
/// empezar a calcularse (reloj monotónico): cubre la visita, que es donde se repetía, y queda muy por debajo de la
/// cota de lectura del alcance memoizado (<c>CaducidadAlcanceOptions</c>, 60 s). Las escrituras no esperan:
/// <see cref="Common.InvalidacionAlcanceBehavior{TRequest,TResponse}"/> llama a <see cref="Invalidar"/> antes y
/// después de cada Command, igual que con el alcance.
/// </para>
///
/// La lista no autoriza nada por sí sola: el POST de cambio de Tenant activo, la revalidación y RLS siguen
/// decidiendo con el dato vigente.
/// </summary>
public sealed class MemoClientesAutorizados(TimeProvider? reloj = null)
{
    /// <summary>Vida máxima de una entrada desde que empezó a calcularse.</summary>
    public static readonly TimeSpan Caducidad = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _reloj = reloj ?? TimeProvider.System;
    private readonly object _cerrojo = new();
    private readonly Dictionary<ClaveMemoClientesAutorizados, Entrada> _entradas = new();

    // Cambia en cada Invalidar. Un cálculo que empezó antes de una invalidación se devuelve a quien lo pidió, pero
    // no se guarda: podría haberse leído antes de la escritura que la provocó.
    private long _generacion;

    private sealed record Entrada(long Inicio, IReadOnlyList<ClienteAutorizadoDto> Lista);

    /// <summary>
    /// La lista memoizada para <paramref name="clave"/> si sigue vigente; si no, la calcula con
    /// <paramref name="calcular"/> y la guarda. Devuelve una colección de solo lectura compartida entre llamadas:
    /// quien necesite modificarla copia antes.
    /// </summary>
    public async Task<IReadOnlyList<ClienteAutorizadoDto>> ObtenerOCalcularAsync(
        ClaveMemoClientesAutorizados clave,
        Func<CancellationToken, Task<IReadOnlyList<ClienteAutorizadoDto>>> calcular,
        CancellationToken cancellationToken)
    {
        long generacion;
        long inicio;
        lock (_cerrojo)
        {
            if (_entradas.TryGetValue(clave, out var entrada))
            {
                if (_reloj.GetElapsedTime(entrada.Inicio) < Caducidad) return entrada.Lista;
                _entradas.Remove(clave);
            }

            generacion = _generacion;
            inicio = _reloj.GetTimestamp();
        }

        var lista = Array.AsReadOnly((await calcular(cancellationToken)).ToArray());

        lock (_cerrojo)
        {
            if (generacion == _generacion)
            {
                // Sin crecer sin límite en un circuito largo: fuera lo caducado de otras claves.
                foreach (var caducada in _entradas.Where(e => _reloj.GetElapsedTime(e.Value.Inicio) >= Caducidad)
                             .Select(e => e.Key).ToList())
                    _entradas.Remove(caducada);
                _entradas[clave] = new Entrada(inicio, lista);
            }
        }

        return lista;
    }

    /// <summary>Descarta todas las entradas del ámbito. Lo llama <c>InvalidacionAlcanceBehavior</c> en cada Command.</summary>
    public void Invalidar()
    {
        lock (_cerrojo)
        {
            _generacion++;
            _entradas.Clear();
        }
    }
}

/// <summary>
/// Todo lo que decide el resultado de <see cref="ObtenerClientesAutorizadosQuery"/> o el contexto RLS con el que se
/// lee (<c>app.tenant_id</c>, <c>app.tenant_origen_id</c>, <c>app.usuario_id</c>): dos llamadas con la misma clave
/// leen lo mismo.
/// </summary>
/// <param name="UsuarioId">Usuario autenticado (claim), el de las carteras y delegaciones.</param>
/// <param name="TenantOrigenId">Tenant de origen del usuario (claim).</param>
/// <param name="PerfilQueDecideAlcance">Perfil de origen que decide qué Asignaciones de Operación alcanzan.</param>
/// <param name="TenantEfectivo">Tenant efectivo (<c>ITenantActual</c>): el Tenant beneficiario o Empresa gestionada activa.</param>
/// <param name="AmbitoTenantExplicito">Tenant fijado por <c>AmbitoTenantExplicito</c> en un fan-out, si lo hay.</param>
public readonly record struct ClaveMemoClientesAutorizados(
    Guid UsuarioId,
    Guid TenantOrigenId,
    string? PerfilQueDecideAlcance,
    Guid? TenantEfectivo,
    Guid? AmbitoTenantExplicito);
