using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Tests;

/// <summary>
/// NavigationManager de prueba que, con <see cref="Retener"/>, no confirma las navegaciones hasta que el test llama a
/// <see cref="Confirmar"/>: reproduce la ventana de Blazor Server en la que <c>NavigateTo</c> ya se pidió pero
/// <see cref="NavigationManager.Uri"/> todavía no refleja el destino. <see cref="Confirmar"/> con una URL que nadie
/// pidió simula un enlace del menú o «atrás»/«adelante» del navegador.
/// </summary>
internal sealed class NavegacionRetenida : NavigationManager
{
    public NavegacionRetenida(string uri = "http://localhost/vehiculos") => Initialize("http://localhost/", uri);

    public bool Retener { get; set; }

    /// <summary>Como en el SSR estático, <c>NavigateTo</c> lanza <see cref="NavigationException"/>.</summary>
    public bool Lanzar { get; set; }

    /// <summary>
    /// Como en Blazor Server, <c>NavigateTo</c> pasa antes por los manejadores de LocationChanging (los de
    /// <c>AvisoCambiosSinGuardar</c>): si uno la detiene, no se pide ni se confirma. Los de los avisos responden sin esperar.
    /// </summary>
    public bool ConAvisos { get; set; }

    public List<string> Destinos { get; } = [];
    public List<NavigationOptions> Opciones { get; } = [];

    protected override void SetNavigationLockState(bool value) { }

    protected override void NavigateToCore(string uri, bool forceLoad) =>
        NavigateToCore(uri, new NavigationOptions { ForceLoad = forceLoad });

    protected override void NavigateToCore(string uri, NavigationOptions options)
    {
        var destino = ToAbsoluteUri(uri).AbsoluteUri;
        if (Lanzar) throw new NavigationException(destino);
        if (ConAvisos)
        {
            var seguir = NotifyLocationChangingAsync(destino, options.HistoryEntryState, isNavigationIntercepted: false);
            if (!seguir.IsCompleted)
                throw new InvalidOperationException("NavegacionRetenida solo admite manejadores de LocationChanging síncronos.");
            if (!seguir.Result) return;
        }
        Destinos.Add(destino);
        Opciones.Add(options);
        if (!Retener) Confirmar(destino);
    }

    public void Confirmar(string destino)
    {
        Uri = destino;
        NotifyLocationChanged(false);
    }
}
