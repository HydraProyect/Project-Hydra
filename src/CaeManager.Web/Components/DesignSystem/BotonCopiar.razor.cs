using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace CaeManager.Web.Components.DesignSystem;

/// <summary>Copia Valor al portapapeles vía clipboard.js y confirma con un Toast.</summary>
public partial class BotonCopiar : ComponentBase, IAsyncDisposable
{
    [Parameter] public string? Valor { get; set; }

    /// <summary>
    /// Alternativa a <see cref="Valor"/> para un dato que no debe cargarse por
    /// adelantado (p. ej. una contraseña: DEC-53/DEC-62 exige que solo se pida
    /// al servidor en el momento de copiarla, nunca como efecto de abrir la
    /// pantalla). Si está presente, el botón se habilita aunque
    /// <see cref="Valor"/> esté vacío, y <c>CopiarAsync</c> lo invoca en vez de
    /// usar <see cref="Valor"/>. Si el resultado llega vacío, no copia nada —
    /// el llamador decide si eso merece su propio Toast.
    /// </summary>
    [Parameter] public Func<Task<string?>>? ValorAsync { get; set; }

    [Parameter] public string Etiqueta { get; set; } = "el valor";

    [Inject] private IJSRuntime JsRuntime { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;

    private IJSObjectReference? _modulo;

    private bool Deshabilitado => ValorAsync is null && string.IsNullOrEmpty(Valor);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
            _modulo = await JsRuntime.InvokeAsync<IJSObjectReference>("import", "./js/clipboard.js");
    }

    private async Task CopiarAsync()
    {
        if (_modulo is null) return;

        try
        {
            // ValorAsync dentro del mismo try que la copia: si es una petición al
            // servidor (el caso de ValorAsync), su fallo se trata igual que un
            // fallo de portapapeles — un Toast, no una excepción escapando del
            // manejador de evento hacia el circuito de Blazor.
            var valor = ValorAsync is not null ? await ValorAsync() : Valor;
            if (string.IsNullOrEmpty(valor)) return;

            await _modulo.InvokeVoidAsync("copiarAlPortapapeles", valor);
            ToastService.Mostrar($"Se copió {Etiqueta} al portapapeles.", TonoToast.Exito);
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos copiar al portapapeles.", TonoToast.Error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_modulo is not null)
            await _modulo.DisposeAsync();
    }
}
