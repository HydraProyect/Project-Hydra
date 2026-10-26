using CaeManager.Application.Common;
using CaeManager.Domain.Common;

namespace CaeManager.Application.EstadoMaterializado.Frescura;

/// <summary>
/// Una lectura de filas de estado materializado que <see cref="CacheEstadoCircuito"/> puede guardar y releer en parte.
/// </summary>
/// <typeparam name="TFila">La fila que devuelve la lectura (una proyección inmutable, nunca una entidad rastreada).</typeparam>
/// <param name="Nombre">
/// Identifica la lectura y sus parámetros (por ejemplo <c>"centros"</c> o <c>"pares-centro:{id}"</c>): dos lecturas con el
/// mismo nombre en el mismo contexto tienen que devolver lo mismo.
/// </param>
/// <param name="EntidadesDeFila">Las entidades (Centro, Trabajador, Empresa o Subcontrata) de las que depende una fila.</param>
/// <param name="LeerTodas">Todas las filas del Tenant de la conexión, <b>sin filtrar por alcance</b> (diseño § 5.2).</param>
/// <param name="LeerDeEntidades">
/// Exactamente las filas de <paramref name="LeerTodas"/> que dependen de alguna de las entidades dadas: ni más (saldrían
/// duplicadas) ni menos (se perderían). Una entidad que ya no tiene filas o que esta conexión no ve no devuelve nada.
/// </param>
public sealed record ConsultaEstadoEnCache<TFila>(
    string Nombre,
    Func<TFila, IEnumerable<Guid>> EntidadesDeFila,
    Func<CancellationToken, Task<IReadOnlyList<TFila>>> LeerTodas,
    Func<IReadOnlyCollection<Guid>, CancellationToken, Task<IReadOnlyList<TFila>>> LeerDeEntidades);

/// <summary>
/// Caché del estado materializado por circuito de Blazor Server (diseño § 5.2 a 5.4). Scoped, nunca singleton: un ámbito es
/// un circuito o una petición de un único usuario.
///
/// <list type="bullet">
/// <item><b>Clave.</b> La de <see cref="ClaveContextoMemo"/> (usuario, Tenant de origen, perfil que decide el alcance, Tenant
/// efectivo y <c>AmbitoTenantExplicito</c>), el nombre de la consulta y el Tenant cuya versión se leyó. Cada entrada guarda
/// además la versión y el día de negocio con que se leyó: otro día es otra lectura (lo de «vence en N días» cambia con el
/// día aunque no cambie la versión).</item>
/// <item><b>Frescura.</b> En cada lectura se lee la versión del Tenant (una sentencia, <see cref="ILectorVersionEstadoTenant"/>)
/// <b>antes</b> que las filas: si coincide con la de la entrada, se sirve la entrada; si subió y el circuito recibió el aviso
/// de cada versión intermedia, se releen solo las filas de las entidades avisadas; si falta algún aviso, se relee entero.
/// Así la caché no caduca por tiempo sino por cambio, y lo que pide la #1613 (no servir datos viejos de un servicio
/// scoped) lo resuelve la versión.</item>
/// <item><b>Sin alcance dentro.</b> Se guardan filas del Tenant sin filtrar por alcance; el alcance lo aplica quien llama,
/// con <c>IAlcanceDatosService</c> y su caducidad, en cada lectura (diseño § 5.2, revisión A2).</item>
/// <item><b>Suscripción.</b> El circuito se suscribe en <see cref="INotificadorEstadoTenant"/> solo a los Tenants cuya fila de
/// versión ha leído con su propio contexto RLS, y se da de baja al liberarse el ámbito (cierre del circuito).</item>
/// <item><b>Retirada de la selección.</b> Si la selección de Tenant beneficiario se retira en caliente
/// (<see cref="IAvisoSeleccionRetirada"/>), la caché suelta en ese momento la suscripción, las entradas y los avisos del
/// Tenant retirado, y no lo vuelve a admitir en lo que le queda al ámbito (dentro de un circuito una selección retirada no
/// vuelve: elegir otra recarga la página): una lectura que estuviera en vuelo con su versión no lo vuelve a suscribir ni
/// guarda nada, y un aviso suyo que ya estuviera publicándose se descarta porque su suscripción ya no consta. Los demás
/// Tenants (el de origen, los leídos con <c>AmbitoTenantExplicito</c>) siguen igual.</item>
/// </list>
///
/// Lo cacheado no autoriza nada: la relectura pasa por el filtro global de EF y por la RLS forzada como cualquier lectura.
/// </summary>
public sealed class CacheEstadoCircuito : IDisposable
{
    /// <summary>Entradas como mucho por circuito; por encima se descarta la usada hace más tiempo.</summary>
    public const int MaximoEntradas = 64;

    /// <summary>Versiones avisadas que se recuerdan por Tenant; un salto mayor se relee entero.</summary>
    public const int MaximoVersionesAvisadas = 256;

    /// <summary>Entidades como mucho en una relectura parcial; con más, se relee entero.</summary>
    public const int MaximoEntidadesEnRelecturaParcial = 500;

    private readonly ICurrentUserService currentUser;
    private readonly ITenantActual tenantActual;
    private readonly ILectorVersionEstadoTenant lectorVersion;
    private readonly INotificadorEstadoTenant notificador;
    private readonly IAvisoSeleccionRetirada? _avisoRetirada;
    private readonly TimeProvider _reloj;
    private readonly object _cerrojo = new();
    private readonly Dictionary<ClaveEntrada, Entrada> _entradas = new();
    private readonly Dictionary<Guid, SortedDictionary<long, HashSet<Guid>>> _avisosPorTenant = new();
    private readonly Dictionary<Guid, IDisposable> _suscripciones = new();
    private long _uso;
    private bool _liberada;

    /// <summary>Tenants beneficiarios cuya selección se retiró en este ámbito: ni se suscriben ni se guardan.</summary>
    private readonly HashSet<Guid> _tenantsRetirados = new();

    /// <param name="tenantActual">
    /// El Tenant del ámbito. Si además avisa de la retirada de la selección (<see cref="IAvisoSeleccionRetirada"/>, como el
    /// de la aplicación web), la caché se da de baja del Tenant retirado en ese momento; si no (fuera de un circuito, dobles
    /// de pruebas), se comporta como antes y solo suelta las suscripciones al liberarse.
    /// </param>
    public CacheEstadoCircuito(
        ICurrentUserService currentUser,
        ITenantActual tenantActual,
        ILectorVersionEstadoTenant lectorVersion,
        INotificadorEstadoTenant notificador,
        TimeProvider? reloj = null)
    {
        this.currentUser = currentUser;
        this.tenantActual = tenantActual;
        this.lectorVersion = lectorVersion;
        this.notificador = notificador;
        _reloj = reloj ?? TimeProvider.System;
        _avisoRetirada = tenantActual as IAvisoSeleccionRetirada;
        if (_avisoRetirada is not null)
            _avisoRetirada.TenantSeleccionadoRetirado += AlRetirarseLaSeleccion;
    }

    private readonly record struct ClaveEntrada(ClaveContextoMemo Contexto, string Consulta, Guid TenantId);

    private sealed record Entrada(long Version, DateOnly Dia, object Filas, long Uso);

    /// <summary>
    /// Se recibió un aviso de un Tenant suscrito. Corre en el hilo de quien publicó: un componente que lo escuche vuelve a
    /// su despachador (<c>InvokeAsync</c>) antes de releer y pintar.
    /// </summary>
    public event Action<AvisoCambioEstado>? CambioRecibido;

    /// <summary>Lecturas enteras hechas (primera lectura, versión sin aviso, otro día).</summary>
    public int RelecturasCompletas { get; private set; }

    /// <summary>Relecturas de solo las filas de las entidades avisadas.</summary>
    public int RelecturasParciales { get; private set; }

    /// <summary>Avisos aceptados (de Tenants suscritos).</summary>
    public int AvisosRecibidos { get; private set; }

    /// <summary>Entradas guardadas ahora de <paramref name="tenantId"/> (para los tests de la retirada de la selección).</summary>
    internal int EntradasDe(Guid tenantId)
    {
        lock (_cerrojo) return _entradas.Keys.Count(c => c.TenantId == tenantId);
    }

    /// <summary>
    /// Las filas de <paramref name="consulta"/> para el contexto actual, de la caché si siguen al día. Sin usuario, sin
    /// Tenant de origen o sin fila de versión visible, lee directamente y no guarda nada.
    /// </summary>
    public async Task<IReadOnlyList<TFila>> ObtenerAsync<TFila>(ConsultaEstadoEnCache<TFila> consulta, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consulta);
        var contexto = await ClaveContextoMemo.ResolverAsync(currentUser, tenantActual);
        if (contexto is null)
            return await consulta.LeerTodas(cancellationToken);

        // La versión, antes que las filas: una escritura que confirme entre las dos lecturas deja filas más nuevas que la
        // versión con que se guardan, y la siguiente lectura las vuelve a pedir. Al revés, se guardarían filas viejas con
        // una versión nueva.
        var version = await lectorVersion.LeerAsync(cancellationToken);
        return await ObtenerDeVersionAsync(consulta, contexto.Value, version, entidadesPedidas: null, cancellationToken);
    }

    /// <summary>
    /// Como <see cref="ObtenerAsync{TFila}(ConsultaEstadoEnCache{TFila}, CancellationToken)"/>, con la versión ya leída por
    /// quien llama en este mismo ámbito (y con su mismo contexto RLS), <b>antes</b> de pedir las filas: los lectores que ya
    /// comprueban que el Tenant está completo leen la versión en esa misma sentencia y no pagan otra. Con <c>null</c> (sin
    /// fila de versión visible, o una identidad que ve varias) o sin contexto que sirva de clave no guarda nada, y entonces no
    /// lee el Tenant entero: solo las filas de <paramref name="entidadesPedidas"/>, las que quien llama va a usar.
    /// </summary>
    internal async Task<IReadOnlyList<TFila>> ObtenerAsync<TFila>(
        ConsultaEstadoEnCache<TFila> consulta, VersionEstadoLeida? versionLeida, IReadOnlyCollection<Guid> entidadesPedidas,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consulta);
        ArgumentNullException.ThrowIfNull(entidadesPedidas);
        var contexto = await ClaveContextoMemo.ResolverAsync(currentUser, tenantActual);
        if (contexto is null || versionLeida is null)
            return await consulta.LeerDeEntidades(entidadesPedidas, cancellationToken);

        return await ObtenerDeVersionAsync(consulta, contexto.Value, versionLeida, entidadesPedidas, cancellationToken);
    }

    private async Task<IReadOnlyList<TFila>> ObtenerDeVersionAsync<TFila>(
        ConsultaEstadoEnCache<TFila> consulta, ClaveContextoMemo contexto, VersionEstadoLeida? version,
        IReadOnlyCollection<Guid>? entidadesPedidas, CancellationToken cancellationToken)
    {
        if (version is null)
            return await consulta.LeerTodas(cancellationToken);

        // Una versión de un Tenant que este ámbito ya no admite (la selección se retiró mientras la lectura estaba en vuelo):
        // ni se suscribe ni se guarda; se lee con el contexto que haya ahora, como sin versión (solo lo pedido, si se sabe).
        if (!Suscribir(version.TenantId))
            return entidadesPedidas is null
                ? await consulta.LeerTodas(cancellationToken)
                : await consulta.LeerDeEntidades(entidadesPedidas, cancellationToken);

        var dia = DiaDeNegocio.Hoy(_reloj);
        var clave = new ClaveEntrada(contexto, consulta.Nombre, version.TenantId);

        IReadOnlyList<TFila>? anteriores = null;
        HashSet<Guid>? avisadas = null;
        lock (_cerrojo)
        {
            if (_entradas.TryGetValue(clave, out var entrada) && entrada.Dia == dia && entrada.Filas is IReadOnlyList<TFila> filas)
            {
                if (entrada.Version == version.Version)
                {
                    _entradas[clave] = entrada with { Uso = ++_uso };
                    return filas;
                }

                if (entrada.Version < version.Version && AvisadasEntre(version.TenantId, entrada.Version, version.Version) is { } entidades)
                {
                    anteriores = filas;
                    avisadas = entidades;
                }
            }

            // La versión bajó (la fila de versión se recreó) o esta lectura llega con una versión anterior a la guardada
            // (otra lectura concurrente guardó antes): lo guardado y los avisos de ese Tenant dejan de servir. La entrada que
            // quede la etiqueta la versión leída antes que sus filas, así que nunca promete más frescura de la que tiene.
            if (entrada is not null && entrada.Version > version.Version)
            {
                _entradas.Remove(clave);
                _avisosPorTenant.Remove(version.TenantId);
            }
        }

        IReadOnlyList<TFila> resultado;
        if (anteriores is not null && avisadas is not null)
        {
            var releidas = await consulta.LeerDeEntidades(avisadas, cancellationToken);
            resultado = anteriores.Where(f => !consulta.EntidadesDeFila(f).Any(avisadas.Contains)).Concat(releidas).ToArray();
            lock (_cerrojo) RelecturasParciales++;
        }
        else
        {
            resultado = (await consulta.LeerTodas(cancellationToken)).ToArray();
            lock (_cerrojo) RelecturasCompletas++;
        }

        Guardar(clave, version.Version, dia, resultado);
        return resultado;
    }

    /// <summary>
    /// Recibe un aviso. Lo llama la suscripción del notificador; un aviso de un Tenant al que este circuito no está
    /// suscrito se ignora. Público para que los tests de aislamiento puedan forzar un aviso equivocado.
    /// </summary>
    public void RecibirAviso(AvisoCambioEstado aviso)
    {
        ArgumentNullException.ThrowIfNull(aviso);
        lock (_cerrojo)
        {
            if (_liberada || !_suscripciones.ContainsKey(aviso.TenantId))
                return;

            if (!_avisosPorTenant.TryGetValue(aviso.TenantId, out var porVersion))
                _avisosPorTenant[aviso.TenantId] = porVersion = new SortedDictionary<long, HashSet<Guid>>();
            if (aviso.Entidades.Count == 0)
            {
                // Un aviso sin entidades no dice qué cambió: esa versión cuenta como no avisada y obliga a releer entero.
                porVersion.Remove(aviso.Version);
            }
            else
            {
                if (!porVersion.TryGetValue(aviso.Version, out var entidades))
                    porVersion[aviso.Version] = entidades = [];
                entidades.UnionWith(aviso.Entidades);
            }
            while (porVersion.Count > MaximoVersionesAvisadas)
                porVersion.Remove(porVersion.Keys.First());
            AvisosRecibidos++;
        }

        try
        {
            CambioRecibido?.Invoke(aviso);
        }
        catch
        {
            // Un componente que falle al enterarse no puede tumbar al que publicó: lo verá en su próxima lectura.
        }
    }

    /// <summary>Se cerró el circuito: fuera las suscripciones y lo guardado.</summary>
    public void Dispose()
    {
        if (_avisoRetirada is not null)
            _avisoRetirada.TenantSeleccionadoRetirado -= AlRetirarseLaSeleccion;

        IDisposable[] suscripciones;
        lock (_cerrojo)
        {
            if (_liberada)
                return;
            _liberada = true;
            suscripciones = [.. _suscripciones.Values];
            _suscripciones.Clear();
            _entradas.Clear();
            _avisosPorTenant.Clear();
        }

        foreach (var suscripcion in suscripciones)
            suscripcion.Dispose();
    }

    /// <summary>
    /// La selección de Tenant beneficiario <paramref name="retirado"/> se retiró en caliente: fuera, ya, su suscripción, sus
    /// entradas y sus avisos, y desde aquí no se admite. Corre en el hilo de quien retira la selección, por eso todo el
    /// estado se toca con el cerrojo; la suscripción se suelta fuera de él, como en <see cref="Dispose"/>, pero sale del
    /// diccionario dentro, así que un aviso que llegue mientras tanto ya se descarta.
    /// </summary>
    private void AlRetirarseLaSeleccion(Guid retirado)
    {
        IDisposable? suelta;
        lock (_cerrojo)
        {
            if (_liberada)
                return;

            _tenantsRetirados.Add(retirado);
            _suscripciones.Remove(retirado, out suelta);
            foreach (var clave in _entradas.Keys.Where(c => c.TenantId == retirado).ToList())
                _entradas.Remove(clave);
            _avisosPorTenant.Remove(retirado);
        }

        suelta?.Dispose();
    }

    /// <summary>Con el cerrojo tomado: un Tenant cuya selección se retiró en este ámbito no se admite.</summary>
    private bool Admite(Guid tenantId) => !_tenantsRetirados.Contains(tenantId);

    /// <summary>
    /// Suscribe este ámbito a <paramref name="tenantId"/> si aún no lo está. Devuelve <c>false</c> si el ámbito ya no admite
    /// ese Tenant (se retiró la selección): entonces ni se suscribe ni hay que guardar nada de él.
    /// </summary>
    private bool Suscribir(Guid tenantId)
    {
        lock (_cerrojo)
        {
            if (!Admite(tenantId))
                return false;
            if (_liberada || _suscripciones.ContainsKey(tenantId))
                return true;
            _suscripciones[tenantId] = notificador.Suscribir(tenantId, RecibirAviso);
            return true;
        }
    }

    /// <summary>
    /// Las entidades avisadas en cada versión de (<paramref name="desde"/>, <paramref name="hasta"/>], o <c>null</c> si falta
    /// el aviso de alguna o son demasiadas. Con el cerrojo tomado.
    /// </summary>
    private HashSet<Guid>? AvisadasEntre(Guid tenantId, long desde, long hasta)
    {
        if (hasta - desde > MaximoVersionesAvisadas || !_avisosPorTenant.TryGetValue(tenantId, out var porVersion))
            return null;

        var entidades = new HashSet<Guid>();
        for (var v = desde + 1; v <= hasta; v++)
        {
            if (!porVersion.TryGetValue(v, out var deVersion))
                return null;
            entidades.UnionWith(deVersion);
            if (entidades.Count > MaximoEntidadesEnRelecturaParcial)
                return null;
        }

        return entidades;
    }

    private void Guardar(ClaveEntrada clave, long version, DateOnly dia, object filas)
    {
        lock (_cerrojo)
        {
            // La selección se retiró entre la suscripción y aquí: lo leído con el Tenant anterior no se guarda.
            if (_liberada || !Admite(clave.TenantId))
                return;

            // Dos lecturas a la vez de la misma clave: gana la de versión más alta (si la versión bajó de verdad, ObtenerAsync
            // ya quitó la entrada antigua).
            if (_entradas.TryGetValue(clave, out var actual) && actual.Dia == dia && actual.Version > version)
                return;

            _entradas[clave] = new Entrada(version, dia, filas, ++_uso);
            while (_entradas.Count > MaximoEntradas)
                _entradas.Remove(_entradas.MinBy(e => e.Value.Uso).Key);

            // Los avisos de versiones que ninguna entrada de ese Tenant va a necesitar ya.
            if (_avisosPorTenant.TryGetValue(clave.TenantId, out var porVersion))
            {
                var minima = _entradas.Where(e => e.Key.TenantId == clave.TenantId).Min(e => e.Value.Version);
                foreach (var vieja in porVersion.Keys.TakeWhile(v => v <= minima).ToList())
                    porVersion.Remove(vieja);
            }
        }
    }
}
