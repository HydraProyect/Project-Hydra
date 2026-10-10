using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace CaeManager.Web.Tests;

/// <summary>
/// <see cref="AtajosListaTeclado"/> registra sus teclas por interop tras el primer pintado. Si la
/// página se abandona antes de que el registro vuelva, <c>DisposeAsync</c> no tiene todavía nada
/// que retirar; el registro que llega después debe retirarse entonces. Mientras siga vivo,
/// <c>atajos-contexto.js</c> cuenta la lista como montada y las fichas 360 ceden j/k/e/f.
/// </summary>
public class AtajosListaTecladoRetiradaTardiaTests : BunitContext
{
    // bUnit no deja preparar a mano una invocación que devuelva IJSObjectReference y resolverla
    // más tarde, que es justo la carrera que se prueba: el runtime entero es un doble propio.
    private readonly ObjetoJsFalso _suscripcion = new();
    private readonly TaskCompletionSource<IJSObjectReference> _registro = new();
    private readonly ObjetoJsFalso _modulo;

    public AtajosListaTecladoRetiradaTardiaTests()
    {
        _modulo = new ObjetoJsFalso { RegistroPendiente = _registro.Task };
        Services.AddSingleton<IJSRuntime>(new RuntimeFalso(_modulo));
    }

    [Fact]
    public async Task Un_registro_que_llega_despues_de_desechar_el_componente_se_retira_al_llegar()
    {
        var cut = Render<AtajosListaTeclado>();
        cut.WaitForAssertion(() => _modulo.Llamadas.Should().Equal("registrarAtajosLista"));

        await cut.Instance.DisposeAsync();
        _suscripcion.Llamadas.Should().BeEmpty("el registro todavía no ha vuelto: no hay nada que retirar");

        await cut.InvokeAsync(() => _registro.SetResult(_suscripcion));

        cut.WaitForAssertion(() => _suscripcion.Llamadas.Should().Equal("dispose"));
        _suscripcion.Desechado.Should().BeTrue();
    }

    [Fact]
    public async Task Un_registro_que_ya_habia_llegado_se_retira_una_sola_vez_al_desechar()
    {
        _registro.SetResult(_suscripcion);

        var cut = Render<AtajosListaTeclado>();
        cut.WaitForAssertion(() => _modulo.Llamadas.Should().Equal("registrarAtajosLista"));
        await cut.Instance.DisposeAsync();
        await cut.Instance.DisposeAsync();

        _suscripcion.Llamadas.Should().Equal("dispose");
    }

    private sealed class RuntimeFalso(ObjetoJsFalso modulo) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult((TValue)(object)modulo);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }

    private sealed class ObjetoJsFalso : IJSObjectReference
    {
        public List<string> Llamadas { get; } = [];
        public bool Desechado { get; private set; }

        /// <summary>Con valor, «registrarAtajosLista» no vuelve hasta que esta tarea termine.</summary>
        public Task<IJSObjectReference>? RegistroPendiente { get; init; }

        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Llamadas.Add(identifier);
            if (identifier == "registrarAtajosLista" && RegistroPendiente is not null)
                return (TValue)await RegistroPendiente;
            return default!;
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);

        public ValueTask DisposeAsync()
        {
            Desechado = true;
            return ValueTask.CompletedTask;
        }
    }
}
