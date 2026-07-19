using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Application.Trabajadores.Commands.ResolverDeteccionAusente;
using CaeManager.Application.Trabajadores.Commands.ResolverDeteccionNuevo;
using CaeManager.Application.Trabajadores.Queries.ObtenerDeteccionesPorEmpresa;
using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Empresas.Pages;

public partial class DeteccionTrabajadores : ComponentBase
{
    [Parameter] public Guid EmpresaId { get; set; }

    private EmpresaDetalleDto? _empresa;
    private IReadOnlyList<DeteccionTrabajadorDto> _detecciones = [];
    private bool _cargando = true;
    private bool _errorCarga;
    private Guid? _procesandoId;

    protected override Task OnInitializedAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            _empresa = await Mediator.Send(new ObtenerEmpresaPorIdQuery(EmpresaId));
            if (_empresa is null)
            {
                _errorCarga = true;
                return;
            }

            var resultado = await Mediator.Send(new ObtenerDeteccionesPorEmpresaQuery(EmpresaId));
            if (resultado.EsFallido)
            {
                _errorCarga = true;
                return;
            }

            _detecciones = resultado.Valor;
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

    private async Task ResolverNuevoAsync(Guid deteccionId, bool crear)
    {
        _procesandoId = deteccionId;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new ResolverDeteccionNuevoCommand(deteccionId, crear));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            ToastService.Mostrar(crear ? "Trabajador dado de alta." : "Detección descartada.", TonoToast.Exito);
            await CargarAsync();
        }
        finally
        {
            _procesandoId = null;
        }
    }

    private async Task ResolverAusenteAsync(Guid deteccionId, bool desactivar)
    {
        _procesandoId = deteccionId;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new ResolverDeteccionAusenteCommand(deteccionId, desactivar));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            ToastService.Mostrar(desactivar ? "Trabajador dado de baja." : "Trabajador mantenido activo.", TonoToast.Exito);
            await CargarAsync();
        }
        finally
        {
            _procesandoId = null;
        }
    }
}
