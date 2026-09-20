using System.Reflection;
using CaeManager.Web.Components.Layout;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace CaeManager.Web.Tests;

/// <summary>
/// Investiga, en aislamiento y sin Postgres ni bUnit (mismo criterio que
/// <c>SelectorTemaGuardadoTrasEscrituraConcurrenteTests</c>, IntegrationTests:
/// se instancia el componente real y se invocan sus métodos protegidos por
/// reflexión), la carrera destapada en CI por PR #710
/// (<c>SelectorTemaTests.El_tema_sobrevive_a_una_navegacion_mejorada_sin_recargar_el_documento</c>,
/// run 35402030148).
///
/// <para>
/// <b>Historia de esta investigación</b> (las dos vueltas anteriores quedan
/// documentadas porque las dos descartaron un diseño, no porque el fichero
/// las use):
/// </para>
/// <list type="number">
/// <item>La hipótesis original de #710 ("<c>CambiarTemaAsync</c> pierde el
/// cambio si el módulo aún no existe") NO se reprodujo: <c>_temaActual</c>
/// se lee en vivo al resolver la importación pendiente, así que el código
/// ya se autocorregía. Verificado con una mutación (capturar el tema en una
/// variable local antes del <c>await</c>): esa mutación SÍ pone el test en
/// rojo, así que no era un test ciego.</item>
/// <item>Revisión de Codex sobre esa conclusión: el primer test tampoco
/// simulaba el SEGUNDO render que Blazor dispara automáticamente al
/// terminar el manejador de <c>@onchange</c> — y ese segundo render SÍ
/// importa. Con <c>_modulo</c> asignado solo tras el <c>await</c>, la
/// guarda de <c>OnAfterRenderAsync</c> no distinguía "no se ha pedido
/// importar" de "la importación sigue en vuelo": ese segundo render
/// arrancaba un SEGUNDO <c>import()</c> (medido: <c>VecesImportado</c>
/// llegaba a 2, hasta 3 con varios cambios apilados).</item>
/// <item>Primer intento de arreglo: que <c>CambiarTemaAsync</c> también
/// esperase la importación compartida. Segunda revisión de Codex lo
/// refutó con tres hallazgos reales — bloquear <c>GuardarTemaAsync</c>
/// pierde la preferencia si el circuito se cierra antes de que el módulo
/// importe; varias continuaciones sobre la misma tarea no tienen orden de
/// reanudación garantizado; y esperar esa tarea en <c>DisposeAsync</c>
/// puede colgarse si el circuito muere a mitad de la llamada a
/// <c>import()</c>. Descartado.</item>
/// </list>
/// <para>
/// <b>Diseño final</b>: un campo NUEVO, <c>_moduloTask</c>, que solo sirve
/// para deduplicar el <c>import()</c> en <c>OnAfterRenderAsync</c> —
/// asignado SÍNCRONAMENTE antes del <c>await</c>, así que un segundo render
/// mientras el primero sigue en vuelo lo ve no-nulo y no pide otra
/// importación. <c>CambiarTemaAsync</c> y <c>DisposeAsync</c> no lo tocan:
/// siguen mirando solo <c>_modulo</c> (el módulo YA resuelto), exactamente
/// como antes de esta PR — <c>GuardarTemaAsync</c> nunca depende de JS
/// interop, y la única continuación pendiente durante una importación en
/// vuelo (la de <c>OnAfterRenderAsync</c>) aplica el <c>_temaActual</c>
/// VIGENTE al resolver, sin ayuda de nadie más.
/// </para>
/// </summary>
public class SelectorTemaModuloPendienteTests
{
    [Fact]
    public async Task Un_render_de_mas_mientras_el_modulo_se_importa_no_dispara_una_segunda_importacion()
    {
        var jsRuntime = new JsRuntimeFalso();
        var selectorTema = CrearSelectorTema(jsRuntime);
        EscribirTemaInicial(selectorTema, "oscuro");

        // Arranca la importación (simula el remontado tras una navegación
        // "enhanced") SIN esperarla — se queda en vuelo a propósito.
        var tareaOnAfterRender = InvocarOnAfterRenderAsync(selectorTema, firstRender: true);
        jsRuntime.VecesImportado.Should().Be(1,
            "OnAfterRenderAsync debe haber arrancado ya la importación en su primer render");

        // El render automático que Blazor dispara tras cualquier evento del
        // circuito mientras la importación sigue en vuelo — el defecto real
        // (medido en CI): esto arrancaba un SEGUNDO import().
        // Sin await hasta completar la importación: con el defecto, este
        // render se queda esperando su propio import() y el test colgaría
        // en vez de fallar por el motivo real (VecesImportado).
        var tareaSegundoRender = InvocarOnAfterRenderAsync(selectorTema, firstRender: false);

        jsRuntime.CompletarImportacion();
        await Task.WhenAll(tareaOnAfterRender, tareaSegundoRender).WaitAsync(TimeSpan.FromSeconds(5));

        jsRuntime.VecesImportado.Should().Be(1,
            "un segundo import() en vuelo no es gratis: dos continuaciones compitiendo por asignar _modulo, y " +
            "la referencia del primero sin liberar nunca — el defecto real medido en CI (PR de seguimiento a #710)");
        jsRuntime.Modulo.TemasAplicados.Should().Equal(["oscuro"]);
    }

    [Fact]
    public async Task Cambiar_el_tema_mientras_el_modulo_se_importa_no_lo_pierde()
    {
        var jsRuntime = new JsRuntimeFalso();
        var selectorTema = CrearSelectorTema(jsRuntime);
        EscribirTemaInicial(selectorTema, "oscuro");

        var tareaOnAfterRender = InvocarOnAfterRenderAsync(selectorTema, firstRender: true);

        // Con la importación aún en vuelo, _modulo sigue null: CambiarTemaAsync
        // no lo aplica en vivo aquí (ver _moduloTask, comentario de diseño),
        // pero TAMPOCO bloquea — completa sin esperar a que el módulo importe,
        // así que awaitarlo directamente no cuelga el test (a diferencia de
        // los diseños descartados, ver el comentario de la clase).
        await InvocarCambiarTemaAsync(selectorTema, "sistema");

        jsRuntime.CompletarImportacion();
        await tareaOnAfterRender.WaitAsync(TimeSpan.FromSeconds(5));

        // La única continuación pendiente (la de OnAfterRenderAsync) lee
        // _temaActual EN VIVO al resolver, no un valor capturado — por eso
        // aplica "sistema" sin que CambiarTemaAsync haya tenido que esperar
        // nada.
        jsRuntime.Modulo.TemasAplicados.Should().Equal(["sistema"],
            "el tema elegido mientras el módulo se importaba tiene que llegar al DOM en cuanto el módulo esté " +
            "listo, no perderse silenciosamente");
        jsRuntime.VecesImportado.Should().Be(1);
    }

    [Fact]
    public async Task Varios_cambios_mientras_el_modulo_se_importa_no_bloquean_ni_repiten_la_importacion()
    {
        var jsRuntime = new JsRuntimeFalso();
        var selectorTema = CrearSelectorTema(jsRuntime);
        EscribirTemaInicial(selectorTema, "oscuro");

        var tareaOnAfterRender = InvocarOnAfterRenderAsync(selectorTema, firstRender: true);

        // Los renders de más no se esperan hasta completar la importación:
        // con el defecto cada uno pediría su propio import() y colgaría el
        // test en vez de dejarlo fallar por VecesImportado.
        await InvocarCambiarTemaAsync(selectorTema, "claro");
        var render2 = InvocarOnAfterRenderAsync(selectorTema, firstRender: false);
        await InvocarCambiarTemaAsync(selectorTema, "sistema");
        var render3 = InvocarOnAfterRenderAsync(selectorTema, firstRender: false);

        jsRuntime.CompletarImportacion();
        await Task.WhenAll(tareaOnAfterRender, render2, render3).WaitAsync(TimeSpan.FromSeconds(5));

        jsRuntime.Modulo.TemasAplicados.Should().Equal(["sistema"],
            "solo el último tema vigente se aplica, en cuanto el módulo esté listo — los intermedios se " +
            "descartan porque nadie los aplicó en vivo mientras la importación seguía en vuelo");
        jsRuntime.VecesImportado.Should().Be(1,
            "tres renders con la importación aún en vuelo (uno por cada cambio, más el inicial) no deben " +
            "disparar más de un import()");
    }

    /// <summary>
    /// Segunda revisión de Codex (2026-09-20): <c>OnAfterRenderAsync</c> leía
    /// <c>_temaActual</c>, lo ELEGIDO. Con la importación en vuelo y un
    /// guardado también en vuelo, aplicaba —y con ello escribía la cookie
    /// que <c>TemaCookie</c> lee en la siguiente petición— un tema que la
    /// cuenta todavía no tenía; si el guardado fallaba después, la cookie
    /// quedaba apuntando a una preferencia inexistente y el circuito de la
    /// página siguiente la revertía. Debe aplicar <c>_temaGuardado</c>, lo
    /// CONFIRMADO. La discrepancia entre ambos campos es exactamente el
    /// estado «hay un guardado en vuelo».
    /// </summary>
    [Fact]
    public async Task Tras_importar_el_modulo_se_aplica_el_tema_confirmado_no_el_que_aun_se_esta_guardando()
    {
        var jsRuntime = new JsRuntimeFalso();
        var selectorTema = CrearSelectorTema(jsRuntime);
        EscribirCampoPrivado(selectorTema, "_temaGuardado", "sistema");
        EscribirCampoPrivado(selectorTema, "_temaActual", "oscuro");

        var tareaOnAfterRender = InvocarOnAfterRenderAsync(selectorTema, firstRender: true);
        jsRuntime.CompletarImportacion();
        await tareaOnAfterRender.WaitAsync(TimeSpan.FromSeconds(5));

        jsRuntime.Modulo.TemasAplicados.Should().Equal(["sistema"],
            "el tema elegido («oscuro») aún se está guardando: aplicarlo escribiría la cookie antes de que la " +
            "cuenta lo tenga, y un fallo posterior del guardado la dejaría apuntando a una preferencia inexistente");
    }

    private static SelectorTema CrearSelectorTema(IJSRuntime jsRuntime)
    {
        var selectorTema = new SelectorTema();
        EscribirPropiedadInyectada(selectorTema, "JsRuntime", jsRuntime);
        return selectorTema;
    }

    private static void EscribirPropiedadInyectada(object instancia, string nombre, object? valor) =>
        (instancia.GetType().GetProperty(nombre, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"No se encontró la propiedad inyectada '{nombre}'."))
            .SetValue(instancia, valor);

    /// <summary>
    /// Estado de un componente ya inicializado: lo elegido y lo confirmado
    /// coinciden (<c>OnInitializedAsync</c> fija los dos a la vez).
    /// </summary>
    private static void EscribirTemaInicial(object instancia, string tema)
    {
        EscribirCampoPrivado(instancia, "_temaActual", tema);
        EscribirCampoPrivado(instancia, "_temaGuardado", tema);
    }

    private static void EscribirCampoPrivado(object instancia, string nombre, object? valor) =>
        (instancia.GetType().GetField(nombre, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"No se encontró el campo privado '{nombre}'."))
            .SetValue(instancia, valor);

    private static Task InvocarOnAfterRenderAsync(SelectorTema selectorTema, bool firstRender)
    {
        var mi = typeof(SelectorTema).GetMethod(
            "OnAfterRenderAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("No se encontró OnAfterRenderAsync.");
        return (Task)mi.Invoke(selectorTema, [firstRender])!;
    }

    private static async Task InvocarCambiarTemaAsync(SelectorTema selectorTema, string tema)
    {
        var mi = typeof(SelectorTema).GetMethod(
            "CambiarTemaAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("No se encontró CambiarTemaAsync.");
        await (Task)mi.Invoke(selectorTema, [new ChangeEventArgs { Value = tema }])!;
    }

    /// <summary>
    /// Dedicado a controlar exactamente cuándo se resuelve <c>import()</c> —
    /// el resto de la interoperación de JS de <c>SelectorTema</c> pasa por
    /// <see cref="Modulo"/> (<see cref="ModuloFalso"/>), no por este objeto.
    /// Una sola <see cref="TaskCompletionSource{TResult}"/> compartida a
    /// propósito: si el código bajo prueba pidiera un SEGUNDO <c>import()</c>
    /// (el propio defecto que este fichero investiga), ambas llamadas
    /// devolverían la misma tarea pendiente — <see cref="VecesImportado"/>
    /// sigue contando cada llamada por separado aunque la tarea sea una sola.
    /// </summary>
    private sealed class JsRuntimeFalso : IJSRuntime
    {
        private readonly TaskCompletionSource<IJSObjectReference> _importacion = new();

        public ModuloFalso Modulo { get; } = new();

        public int VecesImportado { get; private set; }

        public void CompletarImportacion() => _importacion.TrySetResult(Modulo);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (identifier != "import")
                throw new InvalidOperationException($"Llamada JS inesperada en JsRuntimeFalso: {identifier}");

            VecesImportado++;
            return new ValueTask<TValue>((Task<TValue>)(object)_importacion.Task);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }

    private sealed class ModuloFalso : IJSObjectReference
    {
        public List<string> TemasAplicados { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (identifier == "aplicarTema")
                TemasAplicados.Add((string)args![0]!);

            return new ValueTask<TValue>(default(TValue)!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
