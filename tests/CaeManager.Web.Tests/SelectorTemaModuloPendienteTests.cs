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
/// <b>Lo que NO era el defecto.</b> La primera versión de este test solo
/// simulaba una importación en vuelo y comprobaba que el tema elegido
/// mientras tanto llegaba al DOM al resolver — y con eso solo, el código
/// YA se autocorregía: <c>_temaActual</c> se lee en vivo, no capturado, así
/// que la continuación pendiente de <c>OnAfterRenderAsync</c> aplicaba el
/// valor correcto sin ayuda. Verificado con una mutación (capturar el tema
/// en una variable local antes del <c>await</c> en vez de leer el campo):
/// ese test SÍ se pone en rojo con esa mutación, así que no es un test
/// ciego — simplemente el código sin mutar no tenía ese defecto concreto.
/// </para>
///
/// <para>
/// <b>Lo que SÍ era el defecto</b> (hallazgo de revisión, Codex, sobre la
/// primera versión de este arreglo): el primer test tampoco simulaba el
/// SEGUNDO render que Blazor dispara automáticamente al terminar el
/// manejador de <c>@onchange</c> de <see cref="SelectorTema"/> — y ese
/// segundo render SÍ importa. Con el campo antiguo
/// (<c>IJSObjectReference? _modulo</c>, asignado solo tras el <c>await</c>),
/// la guarda <c>_modulo is not null</c> de <c>OnAfterRenderAsync</c> no
/// distingue "no se ha pedido importar todavía" de "la importación sigue en
/// vuelo": ese segundo render, con la primera importación aún sin resolver,
/// arrancaba un SEGUNDO <c>import()</c> — medido aquí como
/// <c>VecesImportado == 2</c> antes del arreglo. La referencia de la
/// primera importación además quedaba sin liberar nunca (la sobrescribía la
/// segunda). El arreglo guarda la <c>Task</c> de importación
/// (<c>_moduloTask</c>) en vez del resultado, para que cualquier llamador
/// —este método o <c>CambiarTemaAsync</c>— espere la MISMA importación en
/// vuelo sin repetirla.
/// </para>
/// </summary>
public class SelectorTemaModuloPendienteTests
{
    [Fact]
    public async Task Cambiar_el_tema_mientras_el_modulo_aun_se_importa_no_pierde_la_eleccion()
    {
        var jsRuntime = new JsRuntimeFalso();
        var selectorTema = CrearSelectorTema(jsRuntime);
        EscribirCampoPrivado(selectorTema, "_temaActual", "oscuro");

        // Arranca la importación del módulo (simula el remontado tras una
        // navegación "enhanced") SIN esperarla — se queda en vuelo a
        // propósito, como en la carrera real.
        var tareaOnAfterRender = InvocarOnAfterRenderAsync(selectorTema, firstRender: true);
        jsRuntime.VecesImportado.Should().Be(1,
            "OnAfterRenderAsync debe haber arrancado ya la importación antes de que el usuario pueda tocar nada");

        // El usuario cambia el tema MIENTRAS el módulo sigue importándose —
        // TAMPOCO se espera: CambiarTemaAsync ahora espera esa MISMA
        // importación (el arreglo), así que awaitarla aquí bloquearía el
        // test hasta CompletarImportacion() más abajo. Fiel a la carrera
        // real: en un circuito de verdad, el evento se despacha y el
        // manejador sigue en vuelo sin que nada más se detenga a esperarlo.
        var tareaCambiarTema = InvocarCambiarTemaAsync(selectorTema, "sistema");

        // El render automático que Blazor dispara al terminar el manejador
        // del evento — SIN esto, el test no cubre nada: es justo el render
        // que arrancaba una segunda importación (hallazgo de revisión,
        // Codex, sobre la primera versión de este test). TAMPOCO se espera
        // aquí: con el código sin arreglar también suspende (arranca su
        // propio import, igual de pendiente), y esperarlo antes de resolver
        // la importación compartida abajo dejaría el test colgado contra
        // ESE código — el control positivo de esta misma prueba.
        var tareaSegundoRender = InvocarOnAfterRenderAsync(selectorTema, firstRender: false);

        // Ahora se resuelve la importación que estaba en vuelo — libera a
        // las tres continuaciones que la esperaban (el OnAfterRenderAsync
        // inicial, el segundo render si llegó a pedir otra importación, y
        // CambiarTemaAsync).
        jsRuntime.CompletarImportacion();
        await tareaOnAfterRender;
        await tareaCambiarTema;
        await tareaSegundoRender;

        // "sistema" puede aparecer más de una vez — lo aplica tanto la
        // continuación de OnAfterRenderAsync (lee _temaActual, ya "sistema"
        // en ese momento) como la de CambiarTemaAsync (su propio texto local,
        // también "sistema"): redundante pero inofensivo, las dos escriben
        // el mismo valor. Lo que NO puede pasar es que el tema elegido no
        // llegue nunca, o que llegue uno distinto.
        jsRuntime.Modulo.TemasAplicados.Should().OnlyContain(tema => tema == "sistema",
            "el tema elegido mientras el módulo se importaba tiene que llegar al DOM en cuanto el módulo esté " +
            "listo, no perderse silenciosamente ni sustituirse por otro");
        jsRuntime.VecesImportado.Should().Be(1,
            "un segundo import() en vuelo no es gratis: dos continuaciones compitiendo por asignar _modulo, y " +
            "la referencia de la primera importación sin liberar nunca — el defecto real medido en CI");
    }

    /// <summary>
    /// Con VARIOS cambios apilados mientras la misma importación sigue en
    /// vuelo, cada <c>CambiarTemaAsync</c> aplica SU PROPIO tema (variable
    /// local, no el campo) en cuanto la importación compartida resuelve —
    /// así que valores intermedios ("claro") pueden llegar a aplicarse antes
    /// de que "sistema" gane, en un orden que este diseño no garantiza (las
    /// continuaciones de varias tareas esperando la misma <c>Task</c> no
    /// tienen un orden de reanudación fijado por el lenguaje). Ese matiz
    /// —de qué orden exacto pinta el DOM durante una ráfaga de cambios
    /// imposible de producir a mano (exige varias selecciones dentro de la
    /// misma importación de un módulo, unos pocos milisegundos)— queda fuera
    /// de lo que este test exige. Lo que SÍ tiene que sostenerse siempre,
    /// pase lo que pase con el orden, es lo medible y lo que de verdad
    /// importa: <c>import()</c> una sola vez, y ningún valor ajeno a los
    /// elegidos.
    /// </summary>
    [Fact]
    public async Task Varios_cambios_mientras_el_modulo_se_importa_no_disparan_una_segunda_importacion()
    {
        var jsRuntime = new JsRuntimeFalso();
        var selectorTema = CrearSelectorTema(jsRuntime);
        EscribirCampoPrivado(selectorTema, "_temaActual", "oscuro");

        var tareaOnAfterRender = InvocarOnAfterRenderAsync(selectorTema, firstRender: true);

        // Ninguna se espera todavía — las cinco suspenden en la misma
        // importación pendiente (o, contra el código sin arreglar, en
        // importaciones pendientes propias): awaitar cualquiera antes de
        // CompletarImportacion() colgaría el test.
        var tareaClaro = InvocarCambiarTemaAsync(selectorTema, "claro");
        var tareaRenderTrasClaro = InvocarOnAfterRenderAsync(selectorTema, firstRender: false);
        var tareaSistema = InvocarCambiarTemaAsync(selectorTema, "sistema");
        var tareaRenderTrasSistema = InvocarOnAfterRenderAsync(selectorTema, firstRender: false);

        jsRuntime.CompletarImportacion();
        await tareaOnAfterRender;
        await tareaClaro;
        await tareaRenderTrasClaro;
        await tareaSistema;
        await tareaRenderTrasSistema;

        jsRuntime.Modulo.TemasAplicados.Should().NotBeEmpty()
            .And.OnlyContain(tema => tema == "claro" || tema == "sistema",
                "ningún valor aplicado puede ser distinto de los que el usuario eligió realmente");
        jsRuntime.Modulo.TemasAplicados.Should().Contain("sistema",
            "el último tema elegido tiene que llegar a aplicarse, aunque no sea necesariamente el último en el orden");
        jsRuntime.VecesImportado.Should().Be(1,
            "tres renders con la importación aún en vuelo (uno por cada cambio, más el inicial) no deben " +
            "disparar más de un import() — el defecto real medido en CI");
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
