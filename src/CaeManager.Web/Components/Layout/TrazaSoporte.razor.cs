using CaeManager.Domain.Soporte;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;

namespace CaeManager.Web.Components.Layout;

/// <summary>
/// Captura la navegación y las interacciones de una sesión de soporte.
///
/// La navegación se toma de <c>LocationChanged</c>, que en Blazor Server ve
/// también los cambios de página que no generan petición HTTP — un middleware
/// se los perdería.
///
/// Las interacciones llegan desde <c>trazaSoporte.js</c>, que las acumula en
/// el navegador y las envía por lotes: un viaje al servidor por clic haría
/// del registro un problema de rendimiento mayor que el que resuelve.
///
/// Todo esto solo se engancha si la sesión es de soporte. Para el resto de
/// usuarios el componente no hace absolutamente nada.
/// </summary>
public partial class TrazaSoporte : ComponentBase, IAsyncDisposable
{
    private bool _esSesionDeSoporte;

    /// <summary>
    /// El plano 3 (ADR-011 § 8), independiente de <see cref="_esSesionDeSoporte"/>
    /// —la vía heredada de <c>DelegacionTenant</c>— y leído directamente de
    /// <see cref="ClienteActivoSeleccionado"/> en vez de por
    /// <c>TrazaSoporteService</c>: ese servicio traza actividad contra una
    /// <c>DelegacionTenant</c> que una sesión privilegiada no tiene, y no hace
    /// falta duplicar esa lógica solo para mostrar un aviso — la auditoría del
    /// plano 3 ya la cubre <c>AuditoriaInterceptor</c> sobre los cambios de la
    /// propia entidad.
    /// </summary>
    private Guid? _sesionPrivilegiadaId;

    private DotNetObjectReference<TrazaSoporte>? _referencia;
    private IJSObjectReference? _modulo;

    protected override async Task OnInitializedAsync()
    {
        _esSesionDeSoporte = await Traza.EsSesionDeSoporteAsync();
        _sesionPrivilegiadaId = ClienteActivoSeleccionado.SesionPrivilegiadaIdSeleccionada;

        if (!_esSesionDeSoporte) return;

        NavigationManager.LocationChanged += AlCambiarDeUbicacion;

        // Se registra como navegación y no como "entró en la organización":
        // el componente se reinicializa en cada carga completa de página, así
        // que marcar aquí la entrada al workspace llenaba la traza de
        // entradas repetidas que no habían ocurrido. El hito real de entrada
        // ya lo marca AccesoConcedido al abrir la ventana; esto solo asegura
        // que la primera pantalla quede anotada, porque LocationChanged solo
        // dispara a partir del segundo cambio.
        await RegistrarAsync(TipoActividadSoporte.Navegacion, RutaRelativa(NavigationManager.Uri));
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || !_esSesionDeSoporte) return;

        _referencia = DotNetObjectReference.Create(this);
        _modulo = await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/trazaSoporte.js");
        await _modulo.InvokeVoidAsync("iniciar", _referencia);
    }

    /// <summary>
    /// Recibe un lote de interacciones desde el navegador. Público e
    /// invocable desde JS por necesidad del interop; no acepta nada que no
    /// sea texto descriptivo, así que no amplía la superficie de ataque.
    /// </summary>
    [JSInvokable]
    public async Task RegistrarInteraccionesAsync(string[] descripciones)
    {
        if (!_esSesionDeSoporte || descripciones.Length == 0) return;

        foreach (var descripcion in descripciones)
            await RegistrarAsync(TipoActividadSoporte.Interaccion, descripcion);
    }

    private async void AlCambiarDeUbicacion(object? remitente, LocationChangedEventArgs argumentos)
    {
        // async void es lo que exige la firma del evento; el try/catch evita
        // que un fallo al registrar derribe el circuito del usuario. Anotar
        // es importante, pero no más que seguir trabajando.
        try
        {
            await RegistrarAsync(TipoActividadSoporte.Navegacion, RutaRelativa(argumentos.Location));
        }
        catch (Exception)
        {
            // Se ignora a propósito: ya se registra en el servicio si procede.
        }
    }

    private Task RegistrarAsync(TipoActividadSoporte tipo, string? detalle) =>
        Traza.RegistrarAsync(tipo, detalle);

    private string RutaRelativa(string uriAbsoluta) =>
        "/" + NavigationManager.ToBaseRelativePath(uriAbsoluta);

    public async ValueTask DisposeAsync()
    {
        if (_esSesionDeSoporte)
            NavigationManager.LocationChanged -= AlCambiarDeUbicacion;

        if (_modulo is not null)
        {
            try
            {
                await _modulo.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // El circuito ya se cerró: no hay módulo que liberar.
            }
        }

        _referencia?.Dispose();
    }
}
