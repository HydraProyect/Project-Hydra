using CaeManager.Web.Features.AtajosGlobales.Recursos;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace CaeManager.Web.Features.AtajosGlobales;

public partial class AtajosGlobales : IAsyncDisposable
{
    [Inject] private IJSRuntime JsRuntime { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    private IJSObjectReference? _modulo;
    private IJSObjectReference? _suscripcion;
    private DotNetObjectReference<AtajosGlobales>? _referenciaDotNet;
    private IJSObjectReference? _moduloKeyTips;
    private IJSObjectReference? _suscripcionKeyTips;
    private bool _ayudaVisible;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;

        _modulo = await JsRuntime.InvokeAsync<IJSObjectReference>("import", "./js/atajos-globales.js");
        _referenciaDotNet = DotNetObjectReference.Create(this);
        _suscripcion = await _modulo.InvokeAsync<IJSObjectReference>("registrarAtajosGlobales", _referenciaDotNet);

        // KeyTips (Alt sola): un único registro para toda la aplicación, como los atajos
        // globales. El módulo no llama a C#: ejecuta el control pulsándolo. Solo necesita los
        // textos de su barra y de su anuncio a lectores de pantalla, ya localizados.
        _moduloKeyTips = await JsRuntime.InvokeAsync<IJSObjectReference>("import", "./js/keytips.js");
        _suscripcionKeyTips = await _moduloKeyTips.InvokeAsync<IJSObjectReference>("registrarKeyTips", new
        {
            raiz = Textos["KeyTipsBarraRaiz"].Value,
            nivel = Textos["KeyTipsBarraNivel"].Value,
            subir = Textos["KeyTipsBarraSubir"].Value,
            salir = Textos["KeyTipsBarraSalir"].Value,
            teclaSubir = Textos["KeyTipsTeclaSubir"].Value,
            teclaSalir = Textos["KeyTipsTeclaSalir"].Value,
            encendido = Textos["KeyTipsAnuncioEncendido"].Value,
            apagado = Textos["KeyTipsAnuncioApagado"].Value
        });
    }

    /// <summary>
    /// Entrada sin teclado a KeyTips, desde la chuleta: la cierra y enciende el modo (el módulo
    /// espera a que el diálogo desaparezca, porque con un Modal abierto no se enciende).
    /// </summary>
    private async Task MostrarKeyTipsAsync()
    {
        _ayudaVisible = false;
        StateHasChanged();
        if (_moduloKeyTips is not null)
            await _moduloKeyTips.InvokeVoidAsync("encenderKeyTips");
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
        // H5 (Project-Hydra-Negocio/tecnico/docs/ux-audit/16-transversales.md): mismo motivo que
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

            if (_suscripcionKeyTips is not null)
            {
                await _suscripcionKeyTips.InvokeVoidAsync("dispose");
                await _suscripcionKeyTips.DisposeAsync();
            }

            if (_moduloKeyTips is not null)
                await _moduloKeyTips.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
        }

        _referenciaDotNet?.Dispose();
    }
}
