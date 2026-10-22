using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using CaeManager.Application.Common;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Components;

/// <summary>
/// Lleva la primera lectura de un listado del prerenderizado al circuito, para que una visita no la haga dos veces.
/// Una página <c>InteractiveServer</c> se ejecuta entera en el prerenderizado (petición HTTP) y otra vez al arrancar
/// el circuito, y cada ejecución pedía la misma página de filas.
///
/// <para>
/// <b>Aislamiento.</b> Lo que viaja (<see cref="PersistentComponentState"/>) va dentro de la propia respuesta HTML,
/// protegido con Data Protection como el resto del estado de Blazor Server, y solo lo lee el circuito que arranca
/// desde esa respuesta. Aun así, el circuito solo lo usa si coinciden:
/// <list type="bullet">
///   <item>el contexto (<see cref="ClaveContextoMemo"/>: usuario, Tenant de origen, perfil que decide el alcance,
///   Tenant efectivo y ámbito explícito), resuelto otra vez en el circuito. Si el usuario cambió de Tenant
///   beneficiario activo en otra pestaña entre la respuesta y el arranque del circuito, no se usa;</item>
///   <item>la consulta, serializada entera (filtros, orden, página, tamaño). Otra pregunta lee de la base.</item>
/// </list>
/// Se usa una sola vez, en la primera carga del circuito; las siguientes leen siempre de la base. Sin contexto
/// (sin usuario o sin Tenant de origen) no se persiste ni se usa nada.
/// </para>
///
/// <para>
/// <b>Frescura.</b> Lo restaurado se leyó en el prerenderizado de esa misma visita, con el mismo contexto y RLS: es
/// lo que el usuario ya está viendo pintado, con la antigüedad del arranque del circuito.
/// </para>
///
/// <para>
/// <b>Fidelidad.</b> Viaja como JSON: <typeparamref name="TValor"/> tiene que sobrevivir a la ida y vuelta sin perder
/// nada que la página pinte (un <c>[JsonIgnore]</c> se perdería). Por eso cada página persiste su propio tipo, no el
/// DTO de la Query si este ignora propiedades.
/// </para>
/// </summary>
public sealed class EstadoPrerenderizado<TValor> : IDisposable where TValor : class
{
    private readonly PersistentComponentState _estado;
    private readonly string _clave;
    private readonly bool _interactivo;
    private readonly PersistingComponentStateSubscription? _suscripcion;
    private ClaveContextoMemo? _contexto;
    private Persistido? _ultimo;
    private Persistido? _pendiente;

    /// <summary>Lo que viaja: el contexto y la consulta con los que se leyó, y el valor.</summary>
    public sealed record Persistido(ClaveContextoMemo Contexto, string Consulta, TValor Valor);

    /// <summary>
    /// Créalo al principio de <c>OnInitializedAsync</c>, antes de cualquier <c>await</c>: en el circuito lo
    /// persistido solo está disponible durante el primer render.
    /// </summary>
    /// <param name="interactivo"><c>RendererInfo.IsInteractive</c> de la página.</param>
    public EstadoPrerenderizado(PersistentComponentState estado, string clave, bool interactivo)
    {
        _estado = estado;
        _clave = clave;
        _interactivo = interactivo;

        if (interactivo)
        {
            if (estado.TryTakeFromJson<Persistido>(clave, out var persistido))
                _pendiente = persistido;
        }
        else
        {
            _suscripcion = estado.RegisterOnPersisting(PersistirAsync, RenderMode.InteractiveServer);
        }
    }

    /// <summary>Fija el contexto de esta ejecución. Sin él no se persiste ni se usa nada.</summary>
    public async Task FijarContextoAsync(ICurrentUserService currentUser, ITenantActual tenantActual) =>
        _contexto = await ClaveContextoMemo.ResolverAsync(currentUser, tenantActual);

    /// <summary>
    /// En el circuito, la primera vez: el valor que el prerenderizado leyó para la misma <paramref name="consulta"/>
    /// y el mismo contexto. Cualquier otra llamada, otra consulta u otro contexto devuelve <c>false</c> y descarta
    /// lo persistido.
    /// </summary>
    public bool TryTomar(object consulta, [NotNullWhen(true)] out TValor? valor)
    {
        var pendiente = _pendiente;
        _pendiente = null;

        if (pendiente is not null && _contexto is { } contexto && pendiente.Contexto == contexto
            && pendiente.Consulta == Serializar(consulta))
        {
            valor = pendiente.Valor;
            return true;
        }

        valor = null;
        return false;
    }

    /// <summary>En el prerenderizado, anota la última lectura para persistirla al terminar.</summary>
    public void Anotar(object consulta, TValor valor)
    {
        if (_interactivo || _contexto is not { } contexto) return;
        _ultimo = new Persistido(contexto, Serializar(consulta), valor);
    }

    /// <summary>En el prerenderizado, descarta lo anotado (p. ej. la lectura no se puede persistir con fidelidad).</summary>
    public void Descartar() => _ultimo = null;

    private Task PersistirAsync()
    {
        if (_ultimo is { } ultimo)
            _estado.PersistAsJson(_clave, ultimo);
        return Task.CompletedTask;
    }

    private static string Serializar(object consulta) => JsonSerializer.Serialize(consulta, consulta.GetType());

    public void Dispose() => _suscripcion?.Dispose();
}
