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
        var ruta = rutaRelativa.Split('?', '#')[0].Trim('/');

        // I-1 (AUDITORIA-USUARIO-AVANZADO-POST-GEN2): "n" solo actúa sobre la
        // base exacta de la lista. Antes se quedaba con el primer segmento, así
        // que en cualquier subruta —/clientes/alta-guiada,
        // /empresas/{id}/deteccion-trabajadores, /trabajadores/{id},
        // /documentos/revision-ia— sacaba al usuario de su flujo para abrir un
        // alta sin relación con lo que tenía delante.
        if (ruta.Contains('/', StringComparison.Ordinal)) return;

        if (CatalogoAtajos.BasesConCreacionRapida.Contains(ruta))
            NavigationManager.NavigateTo($"/{ruta}?accion=crear");
    }

    [JSInvokable]
    public void AlternarAyuda()
    {
        _ayudaVisible = !_ayudaVisible;
        StateHasChanged();
    }

    public async ValueTask DisposeAsync()
    {
        // H5 (docs/ux-audit/16-transversales.md): mismo motivo que
        // AtajosListaTeclado.razor — el circuito puede desconectarse antes
        // de que corra este Dispose.
        try
        {
            if (_suscripcion is not null)
            {
                await _suscripcion.InvokeVoidAsync("dispose");
                await _suscripcion.DisposeAsync();
            }

            if (_modulo is not null)
                await _modulo.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
        }

        _referenciaDotNet?.Dispose();
    }
}
