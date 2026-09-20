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

    /// <summary>
    /// Solo para DEDUPLICAR el <c>import()</c> en <see cref="OnAfterRenderAsync"/>
    /// — no lo usa nadie más (2026-09-19, PR de seguimiento a #710, tras dos
    /// rondas de revisión de Codex; ver <see cref="_modulo"/> para el módulo
    /// en sí, que sigue siendo lo único que <see cref="CambiarTemaAsync"/> y
    /// <see cref="DisposeAsync"/> consultan). Con solo <c>IJSObjectReference?
    /// _modulo</c>, asignado únicamente tras el <c>await</c>, la guarda de
    /// <c>OnAfterRenderAsync</c> (<c>_modulo is not null</c>) no distingue
    /// "todavía no se ha pedido importar" de "la importación sigue en
    /// vuelo": cualquier render de más mientras la primera importación no ha
    /// resuelto —Blazor dispara uno automáticamente tras el evento de
    /// <c>CambiarTemaAsync</c>, por ejemplo— volvía a entrar y pedía un
    /// SEGUNDO <c>import()</c> (medido: <c>VecesImportado</c> llegaba a 2,
    /// incluso a 3 con varios cambios apilados; la referencia del primer
    /// <c>import()</c> además quedaba sin liberar nunca, la sobrescribía la
    /// segunda).
    ///
    /// <para>
    /// La primera versión de este arreglo hacía que <c>CambiarTemaAsync</c>
    /// también esperara esta tarea antes de aplicar el tema en vivo — Codex
    /// lo refutó con tres hallazgos reales: (1) bloquear el guardado en
    /// <c>ApplicationUser</c> hasta que el módulo importe pierde la
    /// preferencia si el circuito se cierra entre medias (la versión base NO
    /// dependía de eso); (2) con varias continuaciones esperando la MISMA
    /// tarea, el orden de reanudación no está garantizado, así que un
    /// cambio anterior podía "ganar" y dejar el DOM en un tema obsoleto; (3)
    /// esperar esta tarea en <see cref="DisposeAsync"/> puede colgarse si el
    /// circuito se desconecta a mitad de la propia llamada a <c>import()</c>
    /// (nadie completa nunca esa tarea). Por eso <c>CambiarTemaAsync</c> y
    /// <c>DisposeAsync</c> no tocan este campo en absoluto: siguen mirando
    /// solo <see cref="_modulo"/>, exactamente como antes de este arreglo, y
    /// se apoyan en que la continuación pendiente de <c>OnAfterRenderAsync</c>
    /// —hay como mucho una— aplica el <c>_temaGuardado</c> CONFIRMADO (lo lee
    /// en el momento, no capturado) en cuanto el módulo esté listo. Hasta el
    /// 2026-09-20 leía <c>_temaActual</c>, lo elegido, y por eso podía escribir
    /// la cookie de un guardado que todavía no había cuajado.
    /// </para>
    /// </summary>
    private Task<IJSObjectReference>? _moduloTask;

    private IJSObjectReference? _modulo;
    private ApplicationUser? _usuario;
    private string? _temaActual;

    /// <summary>
    /// El último tema <b>confirmado</b> en la cuenta (o, sin cuenta, el último
    /// elegido): lo único que puede llegar al navegador, porque <c>aplicarTema</c>
    /// escribe la cookie que <c>TemaCookie</c> lee en la siguiente petición.
    /// <see cref="_temaActual"/> es lo que el usuario ha elegido —lo que
    /// muestra el <c>&lt;select&gt;</c>— y puede ir por delante mientras un
    /// guardado sigue en vuelo, o quedarse por delante para siempre si falla.
    /// Confundirlos es lo que hacía que <see cref="OnAfterRenderAsync"/>
    /// adelantara la cookie a un guardado que aún no había cuajado (segunda
    /// revisión de Codex, 2026-09-20).
    /// </summary>
    private string? _temaGuardado;

    /// <summary>
    /// Hay un manejador drenando cambios. <b>Un solo guardado en vuelo</b>:
    /// dos manejadores solapados mutaban <c>_usuario</c> a la vez —la lambda
    /// diferida de <c>PuertaAccesoDatos</c> lee el campo al cruzar la puerta,
    /// no al crear la operación— y un guardado podía dar <c>Succeeded</c>
    /// sobre la instancia que otro acababa de preparar con SU tema; además,
    /// con dos <c>UPDATE</c> concurrentes gana el que termine último, no el
    /// que se pidió último. El resto de manejadores solo anotan lo elegido en
    /// <see cref="_temaActual"/> y salen: ver <see cref="CambiarTemaAsync"/>.
    /// Es un indicador y no un semáforo a propósito: no hay cola de espera
    /// cuyo orden importe, así que no depende de ninguna garantía de equidad.
    /// </summary>
    private bool _cambioEnCurso;

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
        _temaGuardado = _temaActual;
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
        if (_temaGuardado is null || _moduloTask is not null) return;

        _moduloTask = JsRuntime.InvokeAsync<IJSObjectReference>("import", "./js/tema.js").AsTask();
        _modulo = await _moduloTask;

        // El CONFIRMADO, no el elegido: si un cambio está guardándose ahora
        // mismo, aplicar su tema aquí escribiría la cookie antes de que la
        // cuenta lo tenga. Tampoco se pierde: ese cambio, al cuajar, ve
        // _modulo ya asignado y lo aplica él. Ambas lecturas —asignar
        // _modulo y leer _temaGuardado aquí; asignar _temaGuardado y leer
        // _modulo en CambiarTemaAsync— son síncronas dentro de su
        // continuación y el circuito las ejecuta una a una, así que sea cual
        // sea el orden en que caigan, el último tema confirmado llega.
        await _modulo.InvokeVoidAsync("aplicarTema", _temaGuardado);
    }

    /// <summary>
    /// El guardado va <b>antes</b> de aplicar, y ese orden es la corrección de
    /// un defecto real, no una preferencia de estilo: <c>tema.js</c> no solo
    /// pinta <c>data-theme</c> sobre el documento actual — en la misma llamada
    /// escribe la cookie que <see cref="Services.TemaCookie"/> lee al
    /// prerenderizar <c>App.razor</c>. Esa cookie es un compromiso hacia la
    /// SIGUIENTE petición, así que no puede adelantarse a la fila que esa
    /// petición va a leer.
    ///
    /// <para>
    /// Con el orden anterior (aplicar y luego guardar) una navegación que
    /// llegara mientras el <c>UPDATE</c> seguía en vuelo servía el HTML con el
    /// tema nuevo —desde la cookie ya escrita— y acto seguido el circuito de
    /// esa página, que había leído <c>ApplicationUser.Tema</c> todavía sin
    /// actualizar, aplicaba el tema ANTERIOR: quitaba <c>data-theme</c> y
    /// borraba la cookie. El usuario veía revertida la elección que acababa de
    /// hacer, sin una sola excepción en ningún registro —las dos mitades
    /// funcionaban, solo estaban en el orden equivocado—. Es el fallo que
    /// expulsó a la PR #756 de la cola de fusión el 2026-09-20 (run
    /// 35512763740) y, antes, a otras tres PRs correctas los días 12 y 13 de
    /// septiembre con una causa distinta en la misma línea de guardado (ver
    /// <see cref="GuardarTemaAsync"/>). La invariante queda fijada, en la capa
    /// que la garantiza, por
    /// <c>SelectorTemaGuardadoTrasEscrituraConcurrenteTests.El_tema_no_llega_al_navegador_antes_de_estar_guardado_en_la_cuenta</c>
    /// (IntegrationTests).
    /// </para>
    ///
    /// <para>
    /// <b>Guardado confirmado, no solo intentado</b> (primera revisión de
    /// Codex, 2026-09-20): la primera versión de este orden solo garantizaba
    /// «guardar intentado antes de aplicar», porque <see cref="GuardarTemaAsync"/>
    /// sale con normalidad —tras registrar el fallo— cuando el resultado de
    /// Identity es un error no concurrente, cuando la recarga tras un
    /// conflicto no encuentra la cuenta o cuando el reintento también falla.
    /// En esos tres caminos la cuenta se queda con el tema anterior, y aplicar
    /// igualmente escribía la cookie del nuevo: la misma carrera de arriba,
    /// con otra causa. Por eso <see cref="GuardarTemaAsync"/> devuelve si
    /// persistió y solo entonces se aplica.
    /// </para>
    ///
    /// <para>
    /// <b>Lo que cuesta</b>: el tema tarda ahora en verse lo que tarde el
    /// <c>UPDATE</c> por clave primaria (más, en el camino de conflicto, una
    /// recarga y un segundo intento). Es el precio de que lo que se ve sea lo
    /// que está guardado. Si el guardado falla —por excepción o por resultado
    /// fallido— el DOM y la cookie se quedan como estaban, y queda un
    /// desajuste que <b>no se resuelve aquí</b>: el <c>&lt;select&gt;</c>
    /// conserva la opción elegida (<c>_temaActual</c>) mientras la cuenta y el
    /// documento siguen en la anterior, sin aviso al usuario más que el
    /// <c>LogWarning</c>. Es preferible a lo de antes —que pintaba el tema y
    /// dejaba una cookie apuntando a una preferencia inexistente—, pero sigue
    /// siendo un fallo visible solo en el registro. Tampoco se resuelve el
    /// caso de que, tras un fallo, el <c>&lt;select&gt;</c> quede en una
    /// opción distinta de la que la cuenta y el documento conservan:
    /// revertirlo exigiría <c>StateHasChanged</c>, que este componente evita
    /// a propósito (los tests lo invocan sin renderer, ver
    /// <c>SelectorTemaGuardadoTrasEscrituraConcurrenteTests</c>).
    /// </para>
    ///
    /// <para>
    /// <b>Gana lo último que se pidió, en cualquier orden de llegada</b>
    /// (segunda y tercera revisión de Codex). <see cref="_temaActual"/> se
    /// asigna en el acto, síncronamente, al llegar el evento: es la
    /// <i>intención vigente</i>. Solo un manejador a la vez —el que encuentra
    /// <see cref="_cambioEnCurso"/> en falso— guarda y aplica, y lo hace en
    /// bucle mientras lo pedido difiera de lo confirmado
    /// (<see cref="_temaGuardado"/>), releyendo la intención tras cada vuelta.
    /// Los demás manejadores solo la anotan y salen. Consecuencias: (a) no hay
    /// dos guardados en vuelo, así que <c>_usuario</c> no se muta a la vez y
    /// la última escritura en la cuenta es la última intención; (b) una
    /// intención superada antes de guardarse (A→B→A, o B pisado por C) ni
    /// siquiera se guarda: no hay «llegada tarde» posible, porque nada se
    /// encola —la versión anterior serializaba con un <c>SemaphoreSlim</c>,
    /// cuyo orden de adquisición no está garantizado, y con
    /// oscuro→claro→oscuro podía dejar cuenta, documento y cookie en claro
    /// con el <c>&lt;select&gt;</c> en oscuro—; (c) si un guardado falla y
    /// hay una intención más nueva, se intenta esa; si no la hay, el bucle
    /// termina (no reintenta sin fin), y si el usuario vuelve entonces al
    /// tema confirmado no hay nada que guardar. La comprobación y la marca de
    /// <see cref="_cambioEnCurso"/> no tienen <c>await</c> en medio: el
    /// circuito ejecuta las continuaciones de una en una.
    /// </para>
    ///
    /// <para>
    /// <b>La ventana del <c>import()</c> en vuelo</b> —<c>_modulo</c> todavía
    /// null, los primeros instantes del circuito— ya no adelanta la cookie:
    /// <see cref="OnAfterRenderAsync"/> aplica <see cref="_temaGuardado"/>
    /// (lo confirmado), no <see cref="_temaActual"/> (lo elegido). Si un
    /// guardado está en vuelo, esa continuación aplica el tema anterior y el
    /// cambio, al cuajar, ve <c>_modulo</c> ya asignado y aplica el suyo; si
    /// cuaja antes de que el módulo resuelva, la continuación ya lee el nuevo.
    /// No es la sincronización entre continuaciones que Codex refutó dos veces
    /// (ver <see cref="_moduloTask"/>): nada espera a nada, cada lado lee un
    /// valor confirmado en el momento, y por tanto no puede perder la
    /// preferencia si el circuito muere ni colgar <see cref="DisposeAsync"/>.
    /// </para>
    /// </summary>
    private async Task CambiarTemaAsync(ChangeEventArgs e)
    {
        _temaActual = e.Value?.ToString() ?? "sistema";

        if (_cambioEnCurso)
            return;

        _cambioEnCurso = true;
        try
        {
            while (_temaActual != _temaGuardado)
            {
                var texto = _temaActual!;

                if (_usuario is not null && !await GuardarTemaAsync(texto))
                {
                    if (_temaActual == texto)
                        return;

                    continue;
                }

                _temaGuardado = texto;

                if (_modulo is not null)
                    await _modulo.InvokeVoidAsync("aplicarTema", texto);
            }
        }
        finally
        {
            _cambioEnCurso = false;
        }
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
    ///
    /// <para>
    /// Devuelve <c>true</c> solo si la preferencia quedó persistida: el
    /// llamador aplica el tema en el navegador —y con él escribe la cookie— a
    /// partir de ese valor, no de que el guardado «haya terminado».
    /// </para>
    /// </summary>
    private async Task<bool> GuardarTemaAsync(string texto)
    {
        _usuario!.Tema = TextoATema(texto);
        var resultado = await PuertaAccesoDatos.EjecutarAsync(() => UserManager.UpdateAsync(_usuario));
        if (resultado.Succeeded) return true;

        if (!resultado.Errors.Any(error => error.Code == nameof(IdentityErrorDescriber.ConcurrencyFailure)))
        {
            LogFalloAlGuardar(resultado);
            return false;
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
            return false;
        }

        _usuario = usuarioFresco;
        _usuario.Tema = TextoATema(texto);
        var resultadoReintento = await PuertaAccesoDatos.EjecutarAsync(() => UserManager.UpdateAsync(_usuario));
        if (resultadoReintento.Succeeded) return true;

        LogFalloAlGuardar(resultadoReintento);
        return false;
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
        // Sobre _modulo, no _moduloTask: si la importación sigue en vuelo
        // cuando el componente se retira, _modulo todavía es null y no hay
        // nada que liberar todavía — esperar aquí a que _moduloTask resuelva
        // podría colgar este DisposeAsync si el circuito se desconectó a
        // mitad de la propia llamada a import() (nadie va a completar esa
        // tarea nunca). Ver el comentario de _moduloTask.
        if (_modulo is null) return;

        try
        {
            await _modulo.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // El circuito ya se cerró: no hay módulo que liberar.
        }
    }
}
