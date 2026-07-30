using CaeManager.Application.Dashboard.Queries;
using CaeManager.Web.Components.DesignSystem;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.VisionCartera.Pages;

public partial class VisionCartera : ComponentBase
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    private KpisGlobalesDto? _kpis;
    private bool _error;

    protected override Task OnInitializedAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        _error = false;
        _kpis = null;
        StateHasChanged();

        try
        {
            _kpis = await Mediator.Send(new ObtenerKpisGlobalesQuery());
        }
        catch (Exception)
        {
            _error = true;
        }
    }

    private void VerCliente(Guid tenantId)
    {
        var returnUrl = Uri.EscapeDataString("/");
        NavigationManager.NavigateTo($"/cuenta/cliente-activo/{tenantId}?returnUrl={returnUrl}", forceLoad: true);
    }

    private static TonoBadge SlaDocumentalTono(int tasa) => tasa switch
    {
        >= 90 => TonoBadge.Exito,
        >= 70 => TonoBadge.Advertencia,
        _ => TonoBadge.Peligro
    };
}
