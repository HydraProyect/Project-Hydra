using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace CaeManager.Web.Features.AtajosGlobales;

public partial class AtajosGlobales : ComponentBase, IAsyncDisposable
{
    [Inject] private IJSRuntime JsRuntime { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    private IJSObjectReference? _modulo;
    private IJSObjectReference? _suscripcion;
    private DotNetObjectReference<AtajosGlobales>? _referenciaDotNet;
    private bool _ayudaVisible;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;

        _modulo = await JsRuntime.InvokeAsync<IJSObjectReference>("import", "./js/atajos-globales.js");
        _referenciaDotNet = DotNetObjectReference.Create(this);
        _suscripcion = await _modulo.InvokeAsync<IJSObjectReference>("registrarAtajosGlobales", _referenciaDotNet);
    }

    [JSInvokable]
    public void IrA(string tecla)
    {
        if (CatalogoAtajos.DestinosNavegacion.TryGetValue(tecla, out var destino))
            NavigationManager.NavigateTo(destino);
    }

    [JSInvokable]
    public void CrearAqui()
    {
        var rutaRelativa = NavigationManager.ToBaseRelativePath(NavigationManager.Uri);
        var baseRuta = rutaRelativa.Split('?', '/')[0];

        if (CatalogoAtajos.BasesConCreacionRapida.Contains(baseRuta))
            NavigationManager.NavigateTo($"/{baseRuta}?accion=crear");
    }

    [JSInvokable]
    public void AlternarAyuda()
    {
        _ayudaVisible = !_ayudaVisible;
        StateHasChanged();
    }

    public async ValueTask DisposeAsync()
    {
        if (_suscripcion is not null)
        {
            await _suscripcion.InvokeVoidAsync("dispose");
            await _suscripcion.DisposeAsync();
        }

        if (_modulo is not null)
            await _modulo.DisposeAsync();

        _referenciaDotNet?.Dispose();
    }
}
