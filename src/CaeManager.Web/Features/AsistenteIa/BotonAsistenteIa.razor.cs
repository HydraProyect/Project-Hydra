using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Web.Features.AsistenteIa.Recursos;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace CaeManager.Web.Features.AsistenteIa;

public partial class BotonAsistenteIa : IDisposable
{
    [Inject] private AsistenteIaService AsistenteIaService { get; set; } = default!;
    [Inject] private DisponibilidadAsistente Disponibilidad { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosAsistenteIa> Textos { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private IJSRuntime JsRuntime { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;

    private bool _enPantallaCompleta;

    /// <summary>
    /// La pista de primera vez. Empieza oculta: solo se enseña cuando vega.js confirma que
    /// este usuario no la ha cerrado nunca, para que no parpadee en cada pantalla.
    /// </summary>
    private bool _mostrarPista;

    private string? _usuario;
    private IJSObjectReference? _modulo;

    protected override void OnInitialized()
    {
        _enPantallaCompleta = Vega.EsPantallaCompleta(NavigationManager.ToBaseRelativePath(NavigationManager.Uri));
        NavigationManager.LocationChanged += AlCambiarDeRuta;
        AsistenteIaService.Cambio += AlCambiarVega;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || !Disponibilidad.Alguna) return;

        var estado = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        _usuario = estado.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonimo";
        _modulo = await JsRuntime.InvokeAsync<IJSObjectReference>("import", "./js/vega.js");
        var vista = await _modulo.InvokeAsync<bool>("pistaVista", _usuario);
        if (!vista && !AsistenteIaService.Abierto)
        {
            _mostrarPista = true;
            StateHasChanged();
        }
    }

    private void Abrir() => AsistenteIaService.Abrir();

    private async Task EntendidoAsync()
    {
        _mostrarPista = false;
        await MarcarPistaVistaAsync();
    }

    /// <summary>La pista desaparece para siempre al pulsar Entendido o al abrir Vega.</summary>
    private void AlCambiarVega()
    {
        if (!AsistenteIaService.Abierto || !_mostrarPista) return;
        _ = InvokeAsync(async () =>
        {
            _mostrarPista = false;
            StateHasChanged();
            await MarcarPistaVistaAsync();
        });
    }

    private async Task MarcarPistaVistaAsync()
    {
        if (_modulo is null || _usuario is null) return;
        try
        {
            await _modulo.InvokeVoidAsync("marcarPistaVista", _usuario);
        }
        catch (JSDisconnectedException)
        {
            // Circuito cerrado: la próxima vez la pista vuelve a salir, sin más daño.
        }
    }

    private void AlCambiarDeRuta(object? sender, LocationChangedEventArgs e)
    {
        var enPantalla = Vega.EsPantallaCompleta(NavigationManager.ToBaseRelativePath(e.Location));
        if (enPantalla == _enPantallaCompleta) return;
        _ = InvokeAsync(() =>
        {
            _enPantallaCompleta = enPantalla;
            StateHasChanged();
        });
    }

    public void Dispose()
    {
        NavigationManager.LocationChanged -= AlCambiarDeRuta;
        AsistenteIaService.Cambio -= AlCambiarVega;
        _ = DesecharModuloAsync();
    }

    private async Task DesecharModuloAsync()
    {
        if (_modulo is null) return;
        try
        {
            await _modulo.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
        }
    }
}
