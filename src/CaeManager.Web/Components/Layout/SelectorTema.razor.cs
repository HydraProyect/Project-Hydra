using System.Security.Claims;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace CaeManager.Web.Components.Layout;

/// <summary>
/// Guarda la preferencia de tema en la cuenta del usuario (ApplicationUser.Tema),
/// no en localStorage — así viaja con la cuenta entre dispositivos, igual que
/// cualquier otro ajuste de usuario. Se aplica sobre &lt;html data-theme&gt; vía
/// JS interop, que ocurre después de que el circuito de Blazor conecta (no en
/// el HTML servido inicialmente) — pero <c>wwwroot/js/tema.js</c> también deja
/// una cookie de solo lectura (ver <c>TemaCookie</c>, Web) que el propio
/// servidor lee al prerenderizar <c>App.razor</c>, así que a partir de la
/// SEGUNDA petición con esa cookie el HTML ya sale con el tema correcto y no
/// hay parpadeo. Solo queda un parpadeo posible: la primera vez que este
/// navegador ve a este usuario (cookie ausente, p. ej. tras borrar cookies o
/// en un dispositivo nuevo) — se acepta ese coste igual que antes, a cambio
/// de no consultar <c>ApplicationUser.Tema</c> en cada petición HTML.
/// </summary>
public partial class SelectorTema : ComponentBase, IAsyncDisposable
{
    [Inject] private CaeManager.Application.Common.PuertaAccesoDatos PuertaAccesoDatos { get; set; } = default!;

    /// <summary>
    /// Solo para <see cref="GuardarTemaAsync"/>: <c>UserManager.FindByIdAsync</c>
    /// no sirve para recargar tras un conflicto de concurrencia porque
    /// devuelve la MISMA instancia obsoleta que ya está trackeada en el
    /// DbContext scoped de este circuito (mapa de identidad de EF) — no
    /// repite el viaje a la base. Hace falta desengancharla primero; Web no
    /// resuelve el DbContext concreto (ver
    /// <c>CaeManager.Architecture.Tests.FronterasDeCapaTests</c>), de ahí la
    /// interfaz.
    /// </summary>
    [Inject] private CaeManager.Application.Common.IDesenganchadorDeEntidadesRastreadas Desenganchador { get; set; } = default!;

    [Inject] private ILogger<SelectorTema> Logger { get; set; } = default!;
    [Inject] private ILogger<ExcepcionDeCircuitoDesconectado> LoggerDeCircuitoDesconectado { get; set; } = default!;

    private IJSObjectReference? _modulo;
    private ApplicationUser? _usuario;
    private string? _temaActual;

    protected override async Task OnInitializedAsync()
    {
        var estadoAutenticacion = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        var idClaim = estadoAutenticacion.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (!Guid.TryParse(idClaim, out var usuarioId)) return;

        // Por la puerta: se inicializa en paralelo con el resto del layout
        // sobre el mismo DbContext scoped (ver PuertaAccesoDatos).
        try
        {
            _usuario = await PuertaAccesoDatos.EjecutarAsync(
                () => UserManager.FindByIdAsync(usuarioId.ToString()));
        }
        catch (Exception ex) when (ExcepcionDeCircuitoDesconectado.Es(ex))
        {
            // El circuito de Blazor puede desconectarse (y con él el
            // CaeManagerDbContext scoped) mientras esta consulta sigue en
            // vuelo — reproducido en producción (2026-08-17, mismo evento
            // que MainLayout/SelectorClienteActivo) y ampliado en
            // ExcepcionDeCircuitoDesconectado (REC-166: la misma carrera
            // también puede llegar como ArgumentOutOfRangeException o como
            // una NpgsqlException cruda de desincronización de protocolo,
            // no solo como ObjectDisposedException — este catch solo
            // cubría la primera hasta esta fecha, D-1a,
            // PLAN-SESIONES-NOCTURNAS-2026-09-02.md). _usuario se queda en
            // null, que ya es el valor que la línea de abajo trata como
            // "sin tema guardado" — no hay nadie al otro lado esperando el
            // resultado de todos modos, pero sí queda constancia de que
            // ocurrió.
            LoggerDeCircuitoDesconectado.LogWarning(ex, "SelectorTema descartó una excepción de desconexión de circuito: {TipoExcepcion} — {Mensaje}",
                ex.GetType().Name, ex.Message);
        }

        _temaActual = TemaATexto(_usuario?.Tema ?? TemaPreferido.Sistema);
    }

    /// <summary>
    /// La condición NO es <c>firstRender</c>, y ese era el defecto: como
    /// <see cref="OnInitializedAsync"/> es asíncrono —lee el usuario de la
    /// base—, el primer render ocurre con <c>_temaActual</c> todavía en
    /// <c>null</c>, así que la guarda antigua (<c>!firstRender ||
    /// _temaActual is null</c>) salía por el segundo término. Y en el render
    /// siguiente, ya con el tema resuelto, salía por el primero. Las dos
    /// ramas de la guarda se tapaban entre sí: <c>_modulo</c> no se importaba
    /// <b>nunca</b>, de modo que el tema no se aplicaba ni al cargar la
    /// página ni al cambiarlo en el selector. El cambio sí se guardaba en la
    /// cuenta, así que el fallo era mudo: la preferencia quedaba registrada y
    /// no se veía jamás.
    ///
    /// La condición correcta es "en cuanto se sepa el tema, y una sola vez".
    /// <c>OnAfterRenderAsync</c> no se ejecuta durante el prerenderizado, así
    /// que aquí la interoperación de JS siempre es legal.
    /// </summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_temaActual is null || _modulo is not null) return;

        _modulo = await JsRuntime.InvokeAsync<IJSObjectReference>("import", "./js/tema.js");
        await _modulo.InvokeVoidAsync("aplicarTema", _temaActual);
    }

    private async Task CambiarTemaAsync(ChangeEventArgs e)
    {
        var texto = e.Value?.ToString() ?? "sistema";
        _temaActual = texto;

        if (_modulo is not null)
            await _modulo.InvokeVoidAsync("aplicarTema", texto);

        if (_usuario is not null)
            await GuardarTemaAsync(texto);
    }

    /// <summary>
    /// <c>_usuario</c> se cargó una sola vez en <see cref="OnInitializedAsync"/>
    /// y puede llevar horas en memoria cuando el usuario por fin toca el
    /// selector: cualquier otra escritura sobre esta misma cuenta entre medias
    /// —<c>ActividadUsuarioService</c> toca <c>UltimaActividadUtc</c> en cada
    /// carga de página, con su propio <c>UserManager.UpdateAsync</c>— renueva
    /// <c>ConcurrencyStamp</c> en la base y deja este <c>_usuario</c> obsoleto.
    /// <c>UserManager.UpdateAsync</c> con una entidad obsoleta no lanza: el
    /// <c>UserStore</c> de Identity atrapa <c>DbUpdateConcurrencyException</c> y
    /// devuelve un <see cref="IdentityResult"/> fallido — descartarlo, como
    /// hacía la versión anterior, perdía la preferencia en silencio (mismo
    /// defecto mudo que el que arregló <see cref="OnAfterRenderAsync"/>, esta
    /// vez en el guardado y no en la aplicación). Se recarga la fila fresca y
    /// se reintenta una sola vez: no hay conflicto que fusionar, el tema
    /// elegido ahora mismo siempre debe ganar.
    /// </summary>
    private async Task GuardarTemaAsync(string texto)
    {
        _usuario!.Tema = TextoATema(texto);
        var resultado = await PuertaAccesoDatos.EjecutarAsync(() => UserManager.UpdateAsync(_usuario));
        if (resultado.Succeeded) return;

        if (!resultado.Errors.Any(error => error.Code == nameof(IdentityErrorDescriber.ConcurrencyFailure)))
        {
            LogFalloAlGuardar(resultado);
            return;
        }

        var usuarioFresco = await PuertaAccesoDatos.EjecutarAsync(async () =>
        {
            // Desengancha la instancia obsoleta del change tracker: mientras
            // siga trackeada, FindByIdAsync devuelve exactamente el mismo
            // objeto (mapa de identidad de EF) en vez de repetir la consulta,
            // así que "recargar" no recargaría nada.
            Desenganchador.Desenganchar(_usuario);
            return await UserManager.FindByIdAsync(_usuario.Id.ToString());
        });
        if (usuarioFresco is null)
        {
            Logger.LogWarning(
                "No se pudo recargar la cuenta {UsuarioId} tras un conflicto de concurrencia al guardar el tema.",
                _usuario.Id);
            return;
        }

        _usuario = usuarioFresco;
        _usuario.Tema = TextoATema(texto);
        var resultadoReintento = await PuertaAccesoDatos.EjecutarAsync(() => UserManager.UpdateAsync(_usuario));
        if (!resultadoReintento.Succeeded)
            LogFalloAlGuardar(resultadoReintento);
    }

    private void LogFalloAlGuardar(IdentityResult resultado) =>
        Logger.LogWarning(
            "No se pudo guardar el tema elegido para {UsuarioId}: {Errores}",
            _usuario!.Id, string.Join(" ", resultado.Errors.Select(e => e.Description)));

    private static string TemaATexto(TemaPreferido tema) => tema switch
    {
        TemaPreferido.Claro => "claro",
        TemaPreferido.Oscuro => "oscuro",
        _ => "sistema"
    };

    private static TemaPreferido TextoATema(string texto) => texto switch
    {
        "claro" => TemaPreferido.Claro,
        "oscuro" => TemaPreferido.Oscuro,
        _ => TemaPreferido.Sistema
    };

    public async ValueTask DisposeAsync()
    {
        if (_modulo is not null)
            await _modulo.DisposeAsync();
    }
}
