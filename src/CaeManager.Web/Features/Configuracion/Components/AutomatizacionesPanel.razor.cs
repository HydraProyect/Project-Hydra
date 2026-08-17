using CaeManager.Application.Configuracion.Commands.ActualizarEstadoAutomatizacion;
using CaeManager.Application.Configuracion.Queries.ObtenerEstadoAutomatizaciones;
using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Configuracion.Components;

public partial class AutomatizacionesPanel : ComponentBase
{
    private bool _cargando = true;
    private bool _errorCarga;
    private IReadOnlyList<AutomatizacionDto> _trabajos = [];
    private readonly HashSet<string> _actualizando = [];

    protected override Task OnInitializedAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            _trabajos = await Mediator.Send(new ObtenerEstadoAutomatizacionesQuery());
        }
        catch (Exception)
        {
            _errorCarga = true;
        }
        finally
        {
            _cargando = false;
        }
    }

    private async Task ConmutarAsync(AutomatizacionDto trabajo)
    {
        if (!_actualizando.Add(trabajo.Id)) return;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new ActualizarEstadoAutomatizacionCommand(trabajo.Id, !trabajo.Activo));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            await CargarAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos actualizar el estado. Intenta nuevamente en unos segundos.", TonoToast.Error);
        }
        finally
        {
            _actualizando.Remove(trabajo.Id);
            StateHasChanged();
        }
    }

    private static string FormatearUltimaEjecucion(DateTime? ultimaEjecucionUtc) =>
        ultimaEjecucionUtc is null ? "—" : ultimaEjecucionUtc.Value.ToLocalTime().ToString("dd/MM HH:mm");
}
