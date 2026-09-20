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
/// </summary>
public sealed class HuellaDeSesion(
    ICurrentUserService usuarioActual,
    ITenantActual tenantActual,
    IClienteActivoSeleccionado clienteActivo)
{
    public async Task<string?> ObtenerAsync()
    {
        var usuarioId = await usuarioActual.ObtenerUsuarioActualIdAsync();
        var tenantId = tenantActual.TenantId;
        if (usuarioId is null || tenantId is null)
            return null;

        var rol = await usuarioActual.ObtenerRolActualAsync();
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
    /// el patrón. El instante va DENTRO del estado protegido, así que el
    /// cliente no puede alargarlo.
    /// </summary>
    public static readonly TimeSpan VigenciaMaxima = TimeSpan.FromSeconds(60);

    public EstadoDePantallaPersistido<TInstantanea> Crear<TInstantanea>(string clave)
        where TInstantanea : class =>
        new(estadoPersistente, huellaDeSesion, reloj, clave, registro);
}

/// <summary>
/// Lo que se fija al EMPEZAR una consulta real (ver
/// <see cref="EstadoDePantallaPersistido{T}.EmpezarConsultaAsync"/>): su número,
/// la huella de sesión y el instante. <c>HuellaDeSesion</c> nula = no se
/// persistirá el resultado.
/// </summary>
public readonly record struct ConsultaEnCurso(int Version, string? HuellaDeSesion, DateTimeOffset Instante);

/// <summary>
/// Estado persistido de UNA pantalla. Uso:
/// <list type="number">
/// <item>En <c>OnInitializedAsync</c>: <c>TomarAsync(huellaConsulta)</c>. Si
/// devuelve algo, se aplica y NO se consulta.</item>
/// <item>Si no: se consulta como siempre y, con el resultado, <c>Guardar</c>.
/// El callback de persistencia (fin del prerender) lo escribe en el estado
/// protegido que viaja al navegador.</item>
/// </list>
///
/// <para>
/// <b>Qué viaja al navegador.</b> Solo el resultado de lista que la pantalla
/// ya consulta con el alcance de la sesión (el DTO de lista: puede incluir
/// campos que la pantalla no pinta, siempre de las mismas filas), más la
/// huella de la sesión y el instante. El estado va cifrado y
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
/// </summary>
public sealed class EstadoDePantallaPersistido<TInstantanea> : IDisposable
    where TInstantanea : class
{
    private readonly PersistentComponentState _estado;
    private readonly HuellaDeSesion _huellaDeSesion;
    private readonly TimeProvider _reloj;
    private readonly string _clave;
    private readonly PersistingComponentStateSubscription _suscripcion;

    private TInstantanea? _pendiente;
    private string? _huellaConsultaPendiente;
    private string? _huellaDeSesionPendiente;
    private int _version;
    private DateTimeOffset _tomadaEn;

    private readonly ILogger? _registro;

    internal EstadoDePantallaPersistido(
        PersistentComponentState estado, HuellaDeSesion huellaDeSesion, TimeProvider reloj, string clave,
        ILogger? registro = null)
    {
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
    /// instante y la huella de sesión de la consulta: se sellan con el
    /// contexto en vigor al PREGUNTAR, no al volver la respuesta ni al
    /// persistir (una consulta bajo otro contexto de Tenant no puede quedar
    /// sellada con la huella de otro). La numeración de consultas hace que la
    /// respuesta de una consulta superada por otra más reciente no se anote,
    /// llegue en el orden que llegue.
    /// </summary>
    /// <param name="sePersiste">
    /// <c>false</c> cuando esta pasada no va a persistir (el circuito
    /// interactivo: el prerender ya pasó): así no se paga la resolución de la
    /// huella —que para un workspace delegado consulta la base— en cada acción
    /// del usuario. Las pantallas pasan <c>!RendererInfo.IsInteractive</c>.
    /// </param>
    public async Task<ConsultaEnCurso> EmpezarConsultaAsync(bool sePersiste = true)
    {
        Descartar();
        var version = _version;
        var instante = _reloj.GetUtcNow();
        if (!sePersiste)
            return new ConsultaEnCurso(version, null, instante);

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

        return new ConsultaEnCurso(version, huellaDeSesion, instante);
    }

    /// <summary>
    /// Anota el resultado que el prerender debe dejar al circuito. No anota
    /// nada si otra consulta empezó después de <paramref name="consulta"/> (su
    /// respuesta está superada) o si no había huella de sesión.
    /// </summary>
    public void Guardar(ConsultaEnCurso consulta, string huellaConsulta, TInstantanea instantanea)
    {
        if (consulta.Version != _version || consulta.HuellaDeSesion is null)
            return;

        _pendiente = instantanea;
        _huellaConsultaPendiente = huellaConsulta;
        _huellaDeSesionPendiente = consulta.HuellaDeSesion;
        _tomadaEn = consulta.Instante;
    }

    /// <summary>
    /// Olvida lo anotado. También lo llama cualquier ruta que cambie lo que la
    /// pantalla muestra SIN pasar por una consulta de lista completa (p. ej.
    /// refrescar una sola fila en sitio): lo anotado ya no lo refleja.
    /// </summary>
    public void Descartar()
    {
        _version++;
        _pendiente = null;
        _huellaConsultaPendiente = null;
        _huellaDeSesionPendiente = null;
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

        if (_reloj.GetUtcNow() - sobre.PersistidoEn is var edad
            && (edad < TimeSpan.Zero || edad > FabricaEstadoDePantallaPersistido.VigenciaMaxima))
            return null;

        var huellaActual = await _huellaDeSesion.ObtenerAsync();
        if (huellaActual is null || huellaActual != sobre.HuellaDeSesion)
            return null;

        return sobre.Datos;
    }

    private Task PersistirAsync()
    {
        if (_pendiente is null || _huellaConsultaPendiente is null || _huellaDeSesionPendiente is null)
            return Task.CompletedTask;

        _estado.PersistAsJson(
            _clave, new Sobre(_huellaDeSesionPendiente, _huellaConsultaPendiente, _tomadaEn, _pendiente));
        return Task.CompletedTask;
    }

    public void Dispose() => _suscripcion.Dispose();

    private sealed record Sobre(
        string HuellaDeSesion, string HuellaConsulta, DateTimeOffset PersistidoEn, TInstantanea? Datos);
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
