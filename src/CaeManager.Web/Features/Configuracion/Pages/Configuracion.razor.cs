using CaeManager.Application.Configuracion.Commands.ActualizarParametroSistema;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Web.Components.DesignSystem;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Configuracion.Pages;

public partial class Configuracion : ComponentBase
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;

    private bool _cargando = true;
    private bool _errorCarga;
    private bool _guardando;

    private string _umbralAmbarDias = string.Empty;
    private string _umbralRojoDias = string.Empty;
    private string? _mensajeErrorFormulario;
    private Dictionary<string, string> _erroresCampo = new();

    protected override Task OnInitializedAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            var parametros = await Mediator.Send(new ObtenerParametroSistemaQuery());
            _umbralAmbarDias = parametros.UmbralAmbarDias.ToString();
            _umbralRojoDias = parametros.UmbralRojoDias.ToString();
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

    private async Task GuardarAsync()
    {
        _guardando = true;
        _mensajeErrorFormulario = null;
        _erroresCampo = new Dictionary<string, string>();
        StateHasChanged();

        try
        {
            var umbralAmbar = int.TryParse(_umbralAmbarDias, out var a) ? a : 0;
            var umbralRojo = int.TryParse(_umbralRojoDias, out var r) ? r : 0;

            var resultado = await Mediator.Send(new ActualizarParametroSistemaCommand(umbralAmbar, umbralRojo));

            if (resultado.EsFallido)
            {
                _mensajeErrorFormulario = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar("Umbrales de alerta actualizados correctamente.", TonoToast.Exito);
        }
        catch (ValidationException ex)
        {
            _erroresCampo = ex.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.First().ErrorMessage);
        }
        catch (Exception)
        {
            _mensajeErrorFormulario = "No pudimos guardar los cambios. Intenta nuevamente en unos segundos.";
        }
        finally
        {
            _guardando = false;
        }
    }

    private string? ObtenerError(string campo) => _erroresCampo.GetValueOrDefault(campo);
}
