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
/// Lo memoizado no autoriza nada por sí solo: los endpoints, los Commands y RLS siguen decidiendo con el dato
/// vigente.
/// </summary>
public abstract class MemoPorContexto<TValor>(TimeProvider? reloj = null) : IMemoPorContexto
{
    private readonly TimeProvider _reloj = reloj ?? TimeProvider.System;
    private readonly object _cerrojo = new();
    private readonly Dictionary<ClaveContextoMemo, Entrada> _entradas = new();

    // Cambia en cada Invalidar. Un cálculo que empezó antes de una invalidación se devuelve a quien lo pidió, pero
    // no se guarda: podría haberse leído antes de la escritura que la provocó.
    private long _generacion;

    private sealed record Entrada(long Inicio, TValor Valor);

    /// <summary>
    /// El valor memoizado para <paramref name="clave"/> si sigue vigente; si no, lo calcula con
    /// <paramref name="calcular"/> y lo guarda. El valor se comparte entre llamadas: lo que guarda es lo que
    /// devuelve <see cref="Congelar"/>.
    /// </summary>
    public async Task<TValor> ObtenerOCalcularAsync(
        ClaveContextoMemo clave,
        Func<CancellationToken, Task<TValor>> calcular,
        CancellationToken cancellationToken)
    {
        long generacion;
        long inicio;
        lock (_cerrojo)
        {
            if (_entradas.TryGetValue(clave, out var entrada))
            {
                if (_reloj.GetElapsedTime(entrada.Inicio) < MemoPorContexto.Caducidad) return entrada.Valor;
                _entradas.Remove(clave);
            }

            generacion = _generacion;
            inicio = _reloj.GetTimestamp();
        }

        var valor = Congelar(await calcular(cancellationToken));

        lock (_cerrojo)
        {
            if (generacion == _generacion)
            {
                // Sin crecer sin límite en un circuito largo: fuera lo caducado de otras claves.
                foreach (var caducada in _entradas.Where(e => _reloj.GetElapsedTime(e.Value.Inicio) >= MemoPorContexto.Caducidad)
                             .Select(e => e.Key).ToList())
                    _entradas.Remove(caducada);
                _entradas[clave] = new Entrada(inicio, valor);
            }
        }

        return valor;
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
        {
            _generacion++;
            _entradas.Clear();
        }
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
