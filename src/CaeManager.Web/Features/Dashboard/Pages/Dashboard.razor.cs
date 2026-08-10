using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace CaeManager.Web.Features.Dashboard.Pages;

public partial class Dashboard : ComponentBase
{
    private const int MaximoItemsAtencion = 5;
    private const int MaximoVisitasProximas = 3;

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    private KpisDashboardDto? _kpis;
    private IReadOnlyList<ItemBandejaDto> _requiereAtencion = [];
    private IReadOnlyList<VisitaListaDto> _proximamente = [];
    private bool _error;

    // "Requiere atención" reutiliza la Bandeja del gestor (docs/blueprints/OPERATIONAL-HOME.md
    // § 4) — visible a todos los roles salvo Cliente, mismo alcance que "Documentos que
    // requieren atención" tenía en el Dashboard anterior (_mostrarDocumentosAtencion).
    private bool _mostrarRequiereAtencion;

    protected override Task OnInitializedAsync() => CargarAsync();

    /// <summary>Saludo según la hora del servidor — sustituye el título estático "Dashboard" (ver DESIGN_SYSTEM.md, cabecera con jerarquía).</summary>
    private static string Saludo => DateTime.Now.Hour switch
    {
        < 12 => "Buenos días",
        < 20 => "Buenas tardes",
        _ => "Buenas noches"
    };

    private async Task CargarAsync()
    {
        _error = false;
        _kpis = null;
        StateHasChanged();

        try
        {
            var estadoAutenticacion = await AuthenticationStateProvider.GetAuthenticationStateAsync();
            _mostrarRequiereAtencion = !estadoAutenticacion.User.IsInRole(Roles.Cliente);

            _kpis = await Mediator.Send(new ObtenerKpisDashboardQuery());

            if (!_kpis.SinCarteraAsignada)
            {
                var visitas = await Mediator.Send(new ObtenerVisitasQuery(
                    Busqueda: null, SoloActivas: true, NotificadoCliente: null, TamanoPagina: MaximoVisitasProximas));
                _proximamente = visitas.Elementos;

                if (_mostrarRequiereAtencion)
                {
                    var bandeja = await Mediator.Send(new ObtenerBandejaGestorQuery());
                    _requiereAtencion = [.. bandeja.Take(MaximoItemsAtencion)];
                }
            }
        }
        catch (Exception)
        {
            _error = true;
        }
    }

    private static TonoBadge SlaDocumentalTono(int tasa) => tasa switch
    {
        >= 90 => TonoBadge.Exito,
        >= 70 => TonoBadge.Advertencia,
        _ => TonoBadge.Peligro
    };
}
