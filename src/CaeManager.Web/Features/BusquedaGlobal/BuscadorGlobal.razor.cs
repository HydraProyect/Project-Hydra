using CaeManager.Application.BusquedaGlobal.Queries.BuscarGlobal;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace CaeManager.Web.Features.BusquedaGlobal;

public partial class BuscadorGlobal : ComponentBase
{
    private static readonly TimeSpan RetardoDebounce = TimeSpan.FromMilliseconds(250);

    private IJSObjectReference? _modulo;
    private IJSObjectReference? _suscripcionAtajo;
    private DotNetObjectReference<BuscadorGlobal>? _referenciaDotNet;
    private ElementReference _inputElemento;

    private bool _visible;
    private string _termino = string.Empty;
    private bool _buscando;
    private ResultadoBusquedaGlobalDto? _resultado;
    private CancellationTokenSource? _debounceCts;

    protected override void OnInitialized()
    {
        BusquedaGlobalService.SolicitudAbrir += AbrirDesdeServicio;

        // La navegación mejorada de Blazor reutiliza esta instancia entre
        // páginas (no se recrea el Layout en cada navegación) — sin esto, al
        // hacer clic en un resultado la superposición se quedaba abierta
        // tapando la página de destino.
        Navigation.LocationChanged += ManejarCambioDeUbicacion;
    }

    private void ManejarCambioDeUbicacion(object? sender, LocationChangedEventArgs e)
    {
        if (!_visible) return;

        // LocationChanged no es un evento de Blazor (no dispara StateHasChanged solo) —
        // hay que marshalear al dispatcher del circuito explícitamente.
        _ = InvokeAsync(() =>
        {
            Cerrar();
            StateHasChanged();
        });
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;

        _modulo = await JsRuntime.InvokeAsync<IJSObjectReference>("import", "./js/buscador-global.js");
        _referenciaDotNet = DotNetObjectReference.Create(this);
        _suscripcionAtajo = await _modulo.InvokeAsync<IJSObjectReference>("registrarAtajoBuscador", _referenciaDotNet);
    }

    private void AbrirDesdeServicio() => _ = AbrirAsync();

    [JSInvokable]
    public Task AbrirDesdeJs() => AbrirAsync();

    private async Task AbrirAsync()
    {
        _visible = true;
        _termino = string.Empty;
        _resultado = null;
        StateHasChanged();

        if (_modulo is not null)
        {
            // Espera al siguiente render para que el <input> ya esté en el DOM antes de enfocarlo.
            await Task.Yield();
            await _modulo.InvokeVoidAsync("enfocarElemento", _inputElemento);
        }
    }

    private void Cerrar()
    {
        _visible = false;
        _debounceCts?.Cancel();
    }

    private Task ManejarTeclaAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
            Cerrar();

        return Task.CompletedTask;
    }

    private async Task ManejarEntradaAsync(ChangeEventArgs e)
    {
        _termino = e.Value?.ToString() ?? string.Empty;

        _debounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _debounceCts = cts;

        if (_termino.Trim().Length < 2)
        {
            _resultado = null;
            return;
        }

        try
        {
            _buscando = true;
            await Task.Delay(RetardoDebounce, cts.Token);

            _resultado = await Mediator.Send(new BuscarGlobalQuery(_termino), cts.Token);
        }
        catch (TaskCanceledException)
        {
            // Se canceló porque el usuario siguió escribiendo — ignorar.
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                _buscando = false;
                StateHasChanged();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        BusquedaGlobalService.SolicitudAbrir -= AbrirDesdeServicio;
        Navigation.LocationChanged -= ManejarCambioDeUbicacion;
        _debounceCts?.Cancel();

        if (_suscripcionAtajo is not null)
        {
            await _suscripcionAtajo.InvokeVoidAsync("dispose");
            await _suscripcionAtajo.DisposeAsync();
        }

        if (_modulo is not null)
            await _modulo.DisposeAsync();

        _referenciaDotNet?.Dispose();
    }
}
