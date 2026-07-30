using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Components.Layout;

/// <summary>
/// Cambiar de cliente activo navega (con reload completo) al endpoint
/// <c>/cuenta/cliente-activo/{tenantId}</c> — nunca se cambia en vivo dentro
/// del circuito de Blazor Server. Ver <c>ClienteActivoSeleccionado</c> (Web)
/// para el motivo completo: un cambio puramente en memoria no sobrevive a
/// nada que necesite refrescar la página actual, y un reload sin ese
/// endpoint perdería la elección antes de que ninguna página llegara a
/// leerla.
/// </summary>
public partial class SelectorClienteActivo : ComponentBase
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IClienteActivoSeleccionado ClienteActivoSeleccionado { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    private IReadOnlyList<ClienteAutorizadoDto>? _clientes;
    private Guid _tenantIdActivo;

    protected override async Task OnInitializedAsync()
    {
        _clientes = await Mediator.Send(new ObtenerClientesAutorizadosQuery());

        var activo = _clientes.FirstOrDefault(c => c.TenantId == ClienteActivoSeleccionado.TenantIdSeleccionado)
            ?? _clientes.FirstOrDefault(c => c.EsOrigen);

        if (activo is not null)
            _tenantIdActivo = activo.TenantId;
    }

    private void CambiarCliente(ChangeEventArgs e)
    {
        if (!Guid.TryParse(e.Value?.ToString(), out var tenantId) || tenantId == _tenantIdActivo)
            return;

        var returnUrl = Uri.EscapeDataString(new Uri(NavigationManager.Uri).PathAndQuery);
        NavigationManager.NavigateTo($"/cuenta/cliente-activo/{tenantId}?returnUrl={returnUrl}", forceLoad: true);
    }
}
