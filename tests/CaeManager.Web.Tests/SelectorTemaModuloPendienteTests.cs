using System.Reflection;
using CaeManager.Web.Components.Layout;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace CaeManager.Web.Tests;

/// <summary>
/// Reproduce en aislamiento, sin Postgres ni bUnit (mismo criterio que
/// <c>SelectorTemaGuardadoTrasEscrituraConcurrenteTests</c>, IntegrationTests:
/// se instancia el componente real y se invocan sus métodos protegidos por
/// reflexión), la carrera destapada en CI por PR #710
/// (<c>SelectorTemaTests.El_tema_sobrevive_a_una_navegacion_mejorada_sin_recargar_el_documento</c>,
/// run 35402030148): tras la navegación "enhanced", <c>SelectorTema</c> se
/// remonta y su interoperación de JS (el módulo importado en
/// <c>OnAfterRenderAsync</c>) puede seguir en vuelo cuando el usuario cambia
/// el tema. <c>JsRuntimeFalso</c> deja el <c>import</c> pendiente a
/// voluntad, para forzar exactamente esa ventana sin depender del timing
/// real del navegador.
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

        // El usuario cambia el tema MIENTRAS el módulo sigue importándose.
        await InvocarCambiarTemaAsync(selectorTema, "sistema");

        // Ahora se resuelve la importación que estaba en vuelo.
        jsRuntime.CompletarImportacion();
        await tareaOnAfterRender;

        jsRuntime.Modulo.TemasAplicados.Should().Equal(["sistema"],
            "el tema elegido mientras el módulo se importaba tiene que llegar al DOM en cuanto el módulo esté " +
            "listo, no perderse silenciosamente");
        jsRuntime.VecesImportado.Should().Be(1,
            "un segundo import() en vuelo (aunque el navegador lo resuelva desde caché) no es gratis: reordena " +
            "cuál de las dos continuaciones gana la carrera, y el hallazgo real de CI fue justo una de esas dos " +
            "perdiéndose");
    }

    [Fact]
    public async Task Varios_cambios_mientras_el_modulo_se_importa_solo_aplican_el_ultimo()
    {
        var jsRuntime = new JsRuntimeFalso();
        var selectorTema = CrearSelectorTema(jsRuntime);
        EscribirCampoPrivado(selectorTema, "_temaActual", "oscuro");

        var tareaOnAfterRender = InvocarOnAfterRenderAsync(selectorTema, firstRender: true);

        await InvocarCambiarTemaAsync(selectorTema, "claro");
        await InvocarCambiarTemaAsync(selectorTema, "sistema");

        jsRuntime.CompletarImportacion();
        await tareaOnAfterRender;

        jsRuntime.Modulo.TemasAplicados.Should().Equal(["sistema"],
            "solo debe llegar al DOM el último tema elegido, no cada intermedio ni ninguno de los descartados");
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
