using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace CaeManager.Web.Components.DesignSystem;

/// <summary>Copia Valor al portapapeles vía clipboard.js y confirma con un Toast.</summary>
public partial class BotonCopiar : ComponentBase, IAsyncDisposable
{
    [Parameter] public string? Valor { get; set; }
    [Parameter] public string Etiqueta { get; set; } = "el valor";

    [Inject] private IJSRuntime JsRuntime { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;

    private IJSObjectReference? _modulo;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
            _modulo = await JsRuntime.InvokeAsync<IJSObjectReference>("import", "./js/clipboard.js");
    }

    private async Task CopiarAsync()
    {
        if (string.IsNullOrEmpty(Valor) || _modulo is null) return;

        try
        {
            await _modulo.InvokeVoidAsync("copiarAlPortapapeles", Valor);
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
