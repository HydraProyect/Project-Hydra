using CaeManager.Application.Dashboard.Queries;
using CaeManager.Infrastructure.Identity;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace CaeManager.Web.Features.Dashboard.Pages;

public partial class Dashboard : ComponentBase
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;

    private KpisDashboardDto? _kpis;
    private DesgloseDashboardDto? _desglose;
    private bool _error;

    // Escalera de visibilidad por rol (ver ROADMAP.md, Fase 3): cada rol ve
    // los KPI base más el contenido de todos los roles "por debajo" de él.
    // Consulta no activa ninguna de las tres, EjecutivoCae solo la primera,
    // Supervisor las dos primeras, Administrador las tres.
    private bool _mostrarDocumentosAtencion;
    private bool _mostrarCentrosEnRiesgo;
    private bool _mostrarEmpresasEnRiesgo;

    protected override Task OnInitializedAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        _error = false;
        _kpis = null;
        _desglose = null;
        StateHasChanged();

        try
        {
            var estadoAutenticacion = await AuthenticationStateProvider.GetAuthenticationStateAsync();
            var usuario = estadoAutenticacion.User;

            _mostrarEmpresasEnRiesgo = usuario.IsInRole(Roles.Administrador);
            _mostrarCentrosEnRiesgo = _mostrarEmpresasEnRiesgo || usuario.IsInRole(Roles.Supervisor);
            _mostrarDocumentosAtencion = _mostrarCentrosEnRiesgo || usuario.IsInRole(Roles.EjecutivoCae);

            _kpis = await Mediator.Send(new ObtenerKpisDashboardQuery());

            if (_mostrarDocumentosAtencion)
                _desglose = await Mediator.Send(new ObtenerDesgloseDashboardQuery());
        }
        catch (Exception)
        {
            _error = true;
        }
    }
}
