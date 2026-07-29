using CaeManager.Application.Dashboard.Queries;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace CaeManager.Web.Features.Dashboard.Pages;

public partial class Dashboard : ComponentBase
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    private KpisDashboardDto? _kpis;
    private DesgloseDashboardDto? _desglose;
    private EstadisticasAprobacionDocumentoDto? _estadisticasAprobacion;
    private bool _error;

    // Escalera de visibilidad por rol (ver ROADMAP.md, Fases 3 y 31): cada
    // rol ve los KPI base más el contenido de todos los roles "por debajo"
    // de él. GestorCae ve solo la primera caja, CoordinadorCae las dos
    // primeras, Administrador/DireccionCae/Consulta las tres (Consulta es
    // de solo lectura pero con visibilidad completa). Cliente no tiene
    // enlace a esta página en el menú, pero si llega aquí los datos ya
    // quedan acotados a su propio Cliente por IAlcanceDatosService.
    private bool _mostrarDocumentosAtencion;
    private bool _mostrarCentrosEnRiesgo;
    private bool _mostrarEmpresasEnRiesgo;

    protected override Task OnInitializedAsync() => CargarAsync();

    /// <summary>Saludo según la hora del servidor — sustituye el título estático "Dashboard" (ver DESIGN_SYSTEM.md, cabecera con jerarquía).</summary>
    private static string Saludo => DateTime.Now.Hour switch
    {
        < 12 => "Buenos días",
        < 20 => "Buenas tardes",
        _ => "Buenas noches"
    };

    private void IrADocumento(Guid documentoId) => NavigationManager.NavigateTo($"/documentos?documentoId={documentoId}");

    private void IrACentro(string centroNombre) => NavigationManager.NavigateTo($"/centros?q={Uri.EscapeDataString(centroNombre)}");

    private void IrAEmpresa(string empresaRazonSocial) => NavigationManager.NavigateTo($"/empresas?q={Uri.EscapeDataString(empresaRazonSocial)}");

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

            _mostrarEmpresasEnRiesgo = usuario.IsInRole(Roles.Administrador) || usuario.IsInRole(Roles.DireccionCae) || usuario.IsInRole(Roles.Consulta);
            _mostrarCentrosEnRiesgo = _mostrarEmpresasEnRiesgo || usuario.IsInRole(Roles.CoordinadorCae);
            _mostrarDocumentosAtencion = _mostrarCentrosEnRiesgo || usuario.IsInRole(Roles.GestorCae);

            _kpis = await Mediator.Send(new ObtenerKpisDashboardQuery());

            if (_mostrarDocumentosAtencion)
            {
                _desglose = await Mediator.Send(new ObtenerDesgloseDashboardQuery());
                _estadisticasAprobacion = await Mediator.Send(new ObtenerEstadisticasAprobacionDocumentoQuery());
            }
        }
        catch (Exception)
        {
            _error = true;
        }
    }

    /// <summary>Total de decisiones de verificación IA ya resueltas (automáticas + manuales) — 0 si todavía no se ha verificado ningún Documento.</summary>
    private int TotalAprobaciones => (_estadisticasAprobacion?.Automaticas ?? 0) + (_estadisticasAprobacion?.Manuales ?? 0);

    private int PorcentajeAutomatica => TotalAprobaciones == 0 ? 0 : _estadisticasAprobacion!.Automaticas * 100 / TotalAprobaciones;

    private int PorcentajeManual => TotalAprobaciones == 0 ? 0 : 100 - PorcentajeAutomatica;

    private static TonoBadge SlaDocumentalTono(int tasa) => tasa switch
    {
        >= 90 => TonoBadge.Exito,
        >= 70 => TonoBadge.Advertencia,
        _ => TonoBadge.Peligro
    };
}
