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

    // Baja pendiente de confirmar: «Dar de baja» elimina (soft delete) al
    // trabajador y le cierra las asignaciones, así que no sale de un solo clic.
    private DeteccionTrabajadorDto? _bajaPendiente;
    private bool _confirmarBajaVisible;
    private bool _dandoDeBaja;

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

    /// <summary>
    /// «Mantener activo» no destruye nada —solo da la detección por resuelta— y
    /// por eso sigue siendo un clic. La baja pasa siempre por
    /// <see cref="AbrirConfirmarBaja"/> y <see cref="ConfirmarBajaAsync"/>: ningún
    /// otro camino envía el comando con desactivar a true.
    /// </summary>
    private Task MantenerActivoAsync(Guid deteccionId) => EnviarResolucionAusenteAsync(deteccionId, desactivar: false);

    private void AbrirConfirmarBaja(DeteccionTrabajadorDto deteccion)
    {
        _bajaPendiente = deteccion;
        _confirmarBajaVisible = true;
    }

    private string NombreBajaPendiente =>
        _bajaPendiente is null ? string.Empty : $"{_bajaPendiente.Nombre} {_bajaPendiente.Apellidos}".Trim();

    private async Task ConfirmarBajaAsync()
    {
        if (_bajaPendiente is not { } deteccion)
            return;

        _dandoDeBaja = true;
        try
        {
            if (await EnviarResolucionAusenteAsync(deteccion.Id, desactivar: true))
            {
                _confirmarBajaVisible = false;
                _bajaPendiente = null;
            }
        }
        finally
        {
            _dandoDeBaja = false;
        }
    }

    /// <returns>
    /// <c>true</c> si la operación terminó —se aplicase la baja o no hiciera
    /// falta—; <c>false</c> si falló, ya avisado con un toast. Una excepción
    /// también acaba en <c>false</c> con aviso: antes solo había <c>finally</c>,
    /// el error subía sin decir nada y el diálogo quedaba abierto sin motivo
    /// visible. Mismo criterio que la eliminación de tarifas en Facturación.
    /// </returns>
    private async Task<bool> EnviarResolucionAusenteAsync(Guid deteccionId, bool desactivar)
    {
        _procesandoId = deteccionId;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new ResolverDeteccionAusenteCommand(deteccionId, desactivar));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return false;
            }

            // El mensaje sale de lo que el comando dice que hizo, no de lo que se
            // le pidió: pedir una baja y que el trabajador ya no estuviera activo
            // no es una baja.
            var (mensaje, tono) = resultado.Valor switch
            {
                ResultadoResolucionAusente.DadoDeBaja => ("Trabajador dado de baja.", TonoToast.Exito),
                ResultadoResolucionAusente.YaNoEstabaActivo => (
                    "Este trabajador ya no estaba activo, así que no había nada que dar de baja. La detección queda cerrada.",
                    TonoToast.Info),
                _ => ("Trabajador mantenido activo.", TonoToast.Exito),
            };
            ToastService.Mostrar(mensaje, tono);
            await CargarAsync();
            return true;
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos aplicar el cambio. Intenta nuevamente.", TonoToast.Error);
            return false;
        }
        finally
        {
            _procesandoId = null;
        }
    }
}
