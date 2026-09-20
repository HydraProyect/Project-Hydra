using CaeManager.Application.Common;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CaeManager.Web.Components.EstadoPersistido;

/// <summary>
/// Quién es la sesión que pinta la pantalla, en una sola cadena comparable.
/// La lista que una pantalla enseña depende de todas estas coordenadas —el
/// alcance de datos sale del usuario, su rol y la autorización con la que abrió
/// el workspace—, así que un estado persistido solo vale para la MISMA
/// combinación. <c>null</c> si no hay usuario resuelto: sin identidad no se
/// persiste ni se restaura nada (falla cerrado hacia «consultar otra vez»).
///
/// <para>
/// <b>No toca la base de datos.</b> La huella se resuelve al empezar la
/// pantalla, mientras otros componentes del mismo ámbito de DI (el selector de
/// workspace del layout) hacen sus consultas sobre el mismo <c>DbContext</c>,
/// que no admite dos operaciones a la vez. <c>ObtenerRolActualAsync</c>
/// consulta la cartera cuando hay un workspace delegado seleccionado, así que
/// en ese caso el rol no se pregunta: lo identifican el Tenant seleccionado y la
/// autorización de operación (<c>a=</c>), que ya van en la huella. Consecuencia
/// declarada: si el rol de esa autorización cambia dentro de la vigencia
/// (<see cref="FabricaEstadoDePantallaPersistido.VigenciaMaxima"/>, 10 s), el circuito muestra la instantánea del prerender — la misma clase de
/// hueco que la revocación (el circuito no revalida). Con un ámbito de Tenant
/// explícito (fan-out) no se persiste nada.
/// </para>
/// </summary>
public sealed class HuellaDeSesion(
    ICurrentUserService usuarioActual,
    ITenantActual tenantActual,
    IClienteActivoSeleccionado clienteActivo)
{
    public async Task<string?> ObtenerAsync()
    {
        if (AmbitoTenantExplicito.TenantIdActual is not null)
            return null;

        var usuarioId = await usuarioActual.ObtenerUsuarioActualIdAsync();
        var tenantId = tenantActual.TenantId;
        if (usuarioId is null || tenantId is null)
            return null;

        var rol = clienteActivo.TenantIdSeleccionado is { } seleccionado
            ? $"@{seleccionado}"
            : await usuarioActual.ObtenerRolActualAsync();
        var tenantOrigenId = await usuarioActual.ObtenerTenantOrigenIdAsync();

        return string.Join('|',
            $"u={usuarioId}",
            $"t={tenantId}",
            $"o={tenantOrigenId}",
            $"r={rol}",
            $"a={clienteActivo.AsignacionOperacionIdSeleccionada}",
            $"s={clienteActivo.SesionPrivilegiadaIdSeleccionada}");
    }
}

/// <summary>
/// Crea, para un componente, su
/// <see cref="EstadoDePantallaPersistido{TInstantanea}"/>. Patrón compartido
/// por las pantallas de datos que cargan su lista al montar: la pasada de
/// prerender guarda el resultado y el circuito lo recoge en vez de repetir las
/// mismas consultas (una navegación mejorada ejecutaba la pantalla dos veces:
/// una en el prerender y otra al conectar el circuito).
/// </summary>
public sealed class FabricaEstadoDePantallaPersistido(
    PersistentComponentState estadoPersistente, HuellaDeSesion huellaDeSesion, TimeProvider reloj,
    ILogger<FabricaEstadoDePantallaPersistido>? registro = null)
{
    /// <summary>
    /// Cuánto tiempo puede pasar entre el prerender que guardó el estado y el
    /// circuito que lo recoge. Pasado ese margen el estado se descarta y la
    /// pantalla consulta como si no hubiera nada: nunca es peor que no tener
    /// el patrón. La edad se mide con el reloj MONÓTONO del proceso que sirvió
    /// el prerender (<c>GetTimestamp</c>, no la hora de pared) y el estado lleva
    /// el identificador de ese proceso (<see cref="Instancia"/>): solo el mismo
    /// proceso puede recogerlo. Una hora de pared no sirve para acotar 10 s
    /// entre dos réplicas (relojes desfasados) ni ante un salto de NTP. Marca e
    /// instancia van DENTRO del estado protegido, así que el cliente no puede
    /// alargar la vigencia.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>De dónde sale el número.</b> El estado solo tiene que sobrevivir de la
    /// consulta del prerender a la hidratación (respuesta, arranque de Blazor,
    /// conexión del circuito): cada segundo de más es tiempo en el que una
    /// pérdida de permisos no se refleja (el circuito no revalida). Medido el
    /// 2026-09-20 en la suite E2E completa (81 pruebas, 25 recogidas, máquina de
    /// desarrollo): edad 31–238 ms, mediana 86 ms. 10 s es ~40 veces el máximo
    /// local, margen para un runner de CI lento y una red móvil; quedarse corto
    /// no es un fallo de seguridad sino que el patrón deja de servir (la pantalla
    /// consulta otra vez, como antes). Cada recogida registra su edad y el
    /// tope (<c>Estado persistido de …: edad=… ms</c>) para verificarlo en CI y
    /// en producción y reajustarlo con datos.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan VigenciaMaxima = TimeSpan.FromSeconds(10);

    private static readonly Guid ProcesoActual = Guid.NewGuid();

    /// <summary>
    /// Identifica el proceso que persiste y recoge. Un estado sellado por otra
    /// instancia (otra réplica, o este mismo servicio antes de un reinicio) no
    /// se recoge: su marca monótona no es comparable con la de este proceso.
    /// La propiedad existe para que un test pueda simular otra instancia.
    /// </summary>
    public Guid Instancia { get; init; } = ProcesoActual;

    public EstadoDePantallaPersistido<TInstantanea> Crear<TInstantanea>(string clave)
        where TInstantanea : class =>
        new(estadoPersistente, huellaDeSesion, reloj, clave, Instancia, registro);
}

/// <summary>
/// Lo que se fija al EMPEZAR una consulta real (ver
/// <see cref="EstadoDePantallaPersistido{T}.EmpezarConsultaAsync"/>): su número,
/// la huella de sesión y la marca del reloj monótono, ligados al estado que la
/// emitió (<c>Emisor</c>: un token de otra pantalla no vale). <c>HuellaDeSesion</c>
/// nula = no se persistirá el resultado.
/// </summary>
public readonly record struct ConsultaEnCurso(object? Emisor, int Version, string? HuellaDeSesion, long Marca);

/// <summary>
/// Estado persistido de UNA pantalla. Uso:
/// <list type="number">
/// <item>En <c>OnInitializedAsync</c>: <c>TomarAsync(huellaConsulta)</c>. Si
/// devuelve algo, se aplica y NO se consulta.</item>
/// <item>Si no: <c>EmpezarConsultaAsync(sePersiste)</c> ANTES de consultar
/// (siempre, en cada consulta real); se consulta como siempre; con el
/// resultado ya aplicado a la pantalla, <c>GuardarAsync</c>. El callback de
/// persistencia (fin del prerender) lo escribe en el estado protegido que
/// viaja al navegador.</item>
/// </list>
///
/// <para>
/// <b>Qué viaja al navegador.</b> Solo el resultado de lista que la pantalla
/// ya consulta con el alcance de la sesión (el DTO de lista: puede incluir
/// campos que la pantalla no pinta, siempre de las mismas filas), más la
/// huella de la sesión, la marca de tiempo y la instancia. El estado va cifrado y
/// firmado con Data Protection (el almacén de prerender de Blazor Server): el
/// navegador no lo lee ni lo altera.
/// </para>
///
/// <para>
/// <b>Cuándo se descarta</b> (y la pantalla consulta normalmente): no hay
/// estado; la huella de sesión no coincide (otro usuario, otro Tenant
/// propietario, otro workspace, otro rol o sesión privilegiada); la huella de
/// la consulta no coincide (otros filtros, página o tamaño); o pasó más de
/// <see cref="FabricaEstadoDePantallaPersistido.VigenciaMaxima"/>.
/// </para>
///
/// <para>
/// <b>Lo que el patrón deja de hacer.</b> Mientras dura la vigencia, el
/// circuito NO revalida contra la base: si una cartera o una sesión
/// privilegiada se revoca entre el prerender y el circuito, el circuito enseña
/// una vez las filas que el prerender ya pintó en la misma respuesta HTTP. La
/// huella cubre las coordenadas de la sesión, no el contenido de la cartera.
/// Cualquier acción del usuario después de eso consulta de nuevo.
/// </para>
///
/// <para>
/// <b>Qué filtra las filas y qué se salta la restauración.</b> El filtrado por
/// autorización (<c>IAlcanceDatosService</c>: cartera, Asignación de Operación)
/// y el aislamiento por Tenant (RLS) ocurren <i>dentro de la consulta</i>; el
/// render solo pinta lo que recibe y no vuelve a comprobar nada. Restaurar
/// desde el estado persistido se salta ese filtrado durante la vigencia. La
/// dirección que importa es la de la <b>pérdida</b> de permisos: una
/// Asignación de Operación retirada o una Asignación de Cartera que se estrecha
/// entre el prerender y el circuito no se refleja durante hasta 10 s, y el
/// circuito puede enseñar una vez filas que la autorización vigente ya no
/// permite. Nada de lo que viaja con la sesión (claims, cookie de workspace)
/// cambia cuando cambia una cartera, así que la huella no puede cerrar esa
/// ventana sin una consulta.
/// </para>
/// </summary>
public sealed class EstadoDePantallaPersistido<TInstantanea> : IDisposable
    where TInstantanea : class
{
    private readonly PersistentComponentState _estado;
    private readonly HuellaDeSesion _huellaDeSesion;
    private readonly TimeProvider _reloj;
    private readonly string _clave;
    private readonly Guid _instancia;
    private readonly PersistingComponentStateSubscription _suscripcion;

    private Anotado? _anotado;
    private int _version;

    private readonly ILogger? _registro;

    internal EstadoDePantallaPersistido(
        PersistentComponentState estado, HuellaDeSesion huellaDeSesion, TimeProvider reloj, string clave,
        Guid instancia, ILogger? registro = null)
    {
        _instancia = instancia;
        _registro = registro;
        _estado = estado;
        _huellaDeSesion = huellaDeSesion;
        _reloj = reloj;
        _clave = clave;
        _suscripcion = estado.RegisterOnPersisting(PersistirAsync, RenderMode.InteractiveServer);
    }

    /// <summary>
    /// Se llama ANTES de cada consulta real de la pantalla. Descarta lo
    /// anotado (si esta consulta falla, lo de la anterior ya no refleja lo que
    /// la pantalla muestra: p. ej. la fila que se acaba de eliminar) y fija el
    /// instante y la huella de sesión de la consulta. Al volver la respuesta,
    /// <see cref="GuardarAsync"/> resuelve la huella OTRA VEZ y solo anota si
    /// es idéntica a la de ahora: una consulta que empezó bajo un contexto y
    /// acabó bajo otro no se persiste con ninguno de los dos. La numeración de
    /// consultas hace que la respuesta de una consulta superada por otra más
    /// reciente no se anote, llegue en el orden que llegue.
    /// </summary>
    /// <param name="sePersiste">
    /// OBLIGATORIO, sin valor por defecto: <c>false</c> cuando esta pasada no
    /// va a persistir (el circuito interactivo: el prerender ya pasó), para no
    /// pagar la resolución de la huella —que para un workspace delegado
    /// consulta la base— en cada acción del usuario. Las pantallas pasan
    /// <c>!RendererInfo.IsInteractive</c>. Consecuencia, NO MEDIDA: un circuito
    /// pausado y reanudado vuelve a consultar en vez de recuperar la lista
    /// (pérdida de rendimiento, no de corrección).
    /// </param>
    public async Task<ConsultaEnCurso> EmpezarConsultaAsync(bool sePersiste)
    {
        Descartar();
        var version = _version;
        var marca = _reloj.GetTimestamp();
        if (!sePersiste)
            return new ConsultaEnCurso(this, version, null, marca);

        string? huellaDeSesion;
        try
        {
            huellaDeSesion = await _huellaDeSesion.ObtenerAsync();
        }
        catch (Exception excepcion)
        {
            // Falla cerrado: sin huella no se persiste (la pantalla consulta de
            // nuevo en el circuito). Pero un fallo sostenido apagaría el patrón
            // entero en silencio, así que queda dicho.
            _registro?.LogWarning(
                excepcion, "No se pudo resolver la huella de sesión; el estado de «{Clave}» no se persistirá.", _clave);
            huellaDeSesion = null;
        }

        return new ConsultaEnCurso(this, version, huellaDeSesion, marca);
    }

    /// <summary>
    /// Anota el resultado que el prerender debe dejar al circuito. No anota
    /// nada si la <paramref name="consulta"/> no es de este estado, si otra
    /// consulta empezó después (su respuesta está superada), si no había
    /// huella de sesión, o si la huella de AHORA (al volver la respuesta) no
    /// es idéntica a la de cuando se preguntó. Llamarlo cuando la pantalla ya
    /// tiene aplicado el resultado: es asíncrono y cede el turno.
    /// </summary>
    public async Task GuardarAsync(ConsultaEnCurso consulta, string huellaConsulta, TInstantanea instantanea)
    {
        if (!ReferenceEquals(consulta.Emisor, this) || consulta.Version != _version
            || consulta.HuellaDeSesion is not { } alPreguntar)
            return;

        string? alVolver;
        try
        {
            alVolver = await _huellaDeSesion.ObtenerAsync();
        }
        catch (Exception excepcion)
        {
            _registro?.LogWarning(
                excepcion, "No se pudo resolver la huella de sesión al volver la respuesta; el estado de «{Clave}» no se persistirá.", _clave);
            return;
        }

        // Otra consulta pudo empezar mientras se resolvía la huella.
        if (alVolver != alPreguntar || consulta.Version != _version)
            return;

        _anotado = new Anotado(instantanea, huellaConsulta, alPreguntar, consulta.Marca);
    }

    /// <summary>
    /// Olvida lo anotado. También lo llama cualquier ruta que cambie lo que la
    /// pantalla muestra SIN pasar por una consulta de lista completa (p. ej.
    /// refrescar una sola fila en sitio): lo anotado ya no lo refleja.
    /// </summary>
    public void Descartar()
    {
        _version++;
        _anotado = null;
    }

    /// <summary>
    /// La instantánea que dejó el prerender, si vale para esta sesión y esta
    /// consulta; <c>null</c> en cualquier otro caso.
    /// </summary>
    public async Task<TInstantanea?> TomarAsync(string huellaConsulta)
    {
        if (!_estado.TryTakeFromJson<Sobre>(_clave, out var sobre) || sobre is null)
            return null;

        if (sobre.Datos is null || sobre.HuellaConsulta != huellaConsulta)
            return null;

        // La edad es lo que tarda el camino prerender → hidratación (consulta,
        // render, red hasta el navegador, arranque de Blazor, conexión del
        // circuito): el tope de VigenciaMaxima sale de medir esto, y se deja
        // en el registro para poder comprobarlo también en producción.
        if (sobre.Instancia != _instancia)
        {
            // Otra réplica, o este servicio antes de un reinicio: su marca
            // monótona no es comparable con la de este proceso.
            _registro?.LogInformation(
                "Estado persistido de {Clave}: sellado por otra instancia, no se recoge.", _clave);
            return null;
        }

        var edad = _reloj.GetElapsedTime(sobre.Marca);
        var vigente = edad >= TimeSpan.Zero && edad <= FabricaEstadoDePantallaPersistido.VigenciaMaxima;
        _registro?.LogInformation(
            "Estado persistido de {Clave}: edad={EdadMs} ms, tope={TopeMs} ms, {Resultado}",
            _clave, (long)edad.TotalMilliseconds, (long)FabricaEstadoDePantallaPersistido.VigenciaMaxima.TotalMilliseconds,
            vigente ? "vigente" : "vencido");
        if (!vigente)
            return null;

        var huellaActual = await _huellaDeSesion.ObtenerAsync();
        if (huellaActual is null || huellaActual != sobre.HuellaDeSesion)
            return null;

        return sobre.Datos;
    }

    private Task PersistirAsync()
    {
        if (_anotado is not { } a)
            return Task.CompletedTask;

        _estado.PersistAsJson(_clave, new Sobre(a.HuellaDeSesion, a.HuellaConsulta, _instancia, a.Marca, a.Datos));
        return Task.CompletedTask;
    }

    private sealed record Anotado(
        TInstantanea Datos, string HuellaConsulta, string HuellaDeSesion, long Marca);

    public void Dispose() => _suscripcion.Dispose();

    private sealed record Sobre(
        string HuellaDeSesion, string HuellaConsulta, Guid Instancia, long Marca, TInstantanea? Datos);
}

public static class EstadoPersistidoServiceCollectionExtensions
{
    public static IServiceCollection AddEstadoDePantallaPersistido(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<HuellaDeSesion>();
        services.AddScoped<FabricaEstadoDePantallaPersistido>();
        return services;
    }
}
