using CaeManager.Application.Common;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
    PersistentComponentState estadoPersistente, HuellaDeSesion huellaDeSesion, TimeProvider reloj)
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
        new(estadoPersistente, huellaDeSesion, reloj, clave);
}

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
/// <b>Qué viaja al navegador.</b> Solo la instantánea que la pantalla ya
/// enseña, más la huella de la sesión y el instante. El estado va cifrado y
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
    private DateTimeOffset _tomadaEn;

    internal EstadoDePantallaPersistido(
        PersistentComponentState estado, HuellaDeSesion huellaDeSesion, TimeProvider reloj, string clave)
    {
        _estado = estado;
        _huellaDeSesion = huellaDeSesion;
        _reloj = reloj;
        _clave = clave;
        _suscripcion = estado.RegisterOnPersisting(PersistirAsync, RenderMode.InteractiveServer);
    }

    /// <summary>
    /// Anota el resultado que el prerender debe dejar al circuito. Se llama
    /// con lo que la pantalla acaba de consultar; el instante es el de la
    /// consulta, no el de la persistencia.
    /// </summary>
    public void Guardar(string huellaConsulta, TInstantanea instantanea)
    {
        _pendiente = instantanea;
        _huellaConsultaPendiente = huellaConsulta;
        _tomadaEn = _reloj.GetUtcNow();
    }

    /// <summary>Olvida lo anotado (la pantalla dejó de reflejarlo, p. ej. un error de carga).</summary>
    public void Descartar() => _pendiente = null;

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

    private async Task PersistirAsync()
    {
        if (_pendiente is null || _huellaConsultaPendiente is null)
            return;

        var huellaDeSesion = await _huellaDeSesion.ObtenerAsync();
        if (huellaDeSesion is null)
            return;

        _estado.PersistAsJson(
            _clave, new Sobre(huellaDeSesion, _huellaConsultaPendiente, _tomadaEn, _pendiente));
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
