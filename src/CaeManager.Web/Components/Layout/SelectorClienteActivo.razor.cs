using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace CaeManager.Web.Components.Layout;

/// <summary>
/// Cambiar de cliente activo hace POST (con token antiforgery) al endpoint
/// <c>/cuenta/cliente-activo</c>, que escribe la cookie de workspace y
/// redirige — nunca se cambia en vivo dentro del circuito de Blazor Server.
/// Ver <c>ClienteActivoSeleccionado</c> (Web) para el motivo completo: un
/// cambio puramente en memoria no sobrevive a nada que necesite refrescar la
/// página actual, y un reload sin ese endpoint perdería la elección antes de
/// que ninguna página llegara a leerla.
///
/// Antes era una navegación GET disparada por <c>@onchange</c>. Se cambió a
/// POST por el hallazgo M-8 (cambio de estado sin antiforgery); el efecto
/// secundario es que el selector ya no necesita circuito interactivo para
/// funcionar, porque el envío lo hace el propio navegador.
/// </summary>
public partial class SelectorClienteActivo : ComponentBase
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IClienteActivoSeleccionado ClienteActivoSeleccionado { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private AntiforgeryStateProvider AntiforgeryStateProvider { get; set; } = default!;

    private IReadOnlyList<ClienteAutorizadoDto>? _clientes;
    private Guid _tenantIdActivo;
    private string _returnUrl = "/";
    private AntiforgeryRequestToken? _token;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _clientes = await Mediator.Send(new ObtenerClientesAutorizadosQuery());
        }
        catch (ObjectDisposedException)
        {
            // El circuito de Blazor puede desconectarse (y con él el
            // IServiceProvider del scope, del que el pipeline de MediatR
            // resuelve sus propios behaviors) mientras este despacho
            // sigue en vuelo — reproducido en producción (2026-08-17,
            // mismo evento que MainLayout/SelectorTema, ver
            // Project-Hydra-Negocio/tecnico/d8-vps-evidence.md). Sin
            // lista de clientes no hay nada más que calcular aquí, y no
            // hay nadie al otro lado esperando el resultado de todos
            // modos.
            return;
        }

        var activo = _clientes.FirstOrDefault(c => c.TenantId == ClienteActivoSeleccionado.TenantIdSeleccionado)
            ?? _clientes.FirstOrDefault(c => c.EsOrigen);

        if (activo is not null)
            _tenantIdActivo = activo.TenantId;

        // Volver a la página en la que está el usuario tras el cambio. El
        // endpoint usa LocalRedirect, así que un valor manipulado que no sea
        // una ruta local se rechaza allí.
        _returnUrl = new Uri(NavigationManager.Uri).PathAndQuery;

        _token = AntiforgeryStateProvider.GetAntiforgeryToken();
    }
}
