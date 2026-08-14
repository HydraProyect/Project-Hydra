using CaeManager.Application.Configuracion.Commands.ActualizarConfiguracionOperativa;
using CaeManager.Application.Configuracion.Commands.ActualizarParametroSistema;
using CaeManager.Application.Configuracion.Commands.ActualizarPresupuestoIa;
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

    private bool _guardandoOperativa;
    private string _horaInicioJornada = string.Empty;
    private string _horaFinJornada = string.Empty;
    private string _horasJornadaMensualGestor = string.Empty;
    private bool _medicionTiempoActiva;
    private string _segundosInactividadPausa = string.Empty;
    private bool _excluirFueraDeJornadaEnMetricas;
    private string? _mensajeErrorOperativa;
    private Dictionary<string, string> _erroresCampoOperativa = new();

    private bool _guardandoPresupuestoIa;
    private string _presupuestoMensualIaUsd = string.Empty;
    private string? _mensajeErrorPresupuestoIa;

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
            _horaInicioJornada = parametros.HoraInicioJornada.ToString("HH:mm");
            _horaFinJornada = parametros.HoraFinJornada.ToString("HH:mm");
            _horasJornadaMensualGestor = parametros.HorasJornadaMensualGestor.ToString();
            _medicionTiempoActiva = parametros.MedicionTiempoActiva;
            _segundosInactividadPausa = parametros.SegundosInactividadPausa.ToString();
            _excluirFueraDeJornadaEnMetricas = parametros.ExcluirFueraDeJornadaEnMetricas;
            _presupuestoMensualIaUsd = parametros.PresupuestoMensualIaUsd?.ToString("F2") ?? string.Empty;
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

    private async Task GuardarOperativaAsync()
    {
        _guardandoOperativa = true;
        _mensajeErrorOperativa = null;
        _erroresCampoOperativa = new Dictionary<string, string>();
        StateHasChanged();

        try
        {
            if (!TimeOnly.TryParse(_horaInicioJornada, out var horaInicio) || !TimeOnly.TryParse(_horaFinJornada, out var horaFin))
            {
                _mensajeErrorOperativa = "Las horas de jornada deben tener formato HH:mm.";
                return;
            }

            var resultado = await Mediator.Send(new ActualizarConfiguracionOperativaCommand(
                horaInicio,
                horaFin,
                int.TryParse(_horasJornadaMensualGestor, out var horasMes) ? horasMes : 0,
                _medicionTiempoActiva,
                int.TryParse(_segundosInactividadPausa, out var segundos) ? segundos : 0,
                _excluirFueraDeJornadaEnMetricas));

            if (resultado.EsFallido)
            {
                _mensajeErrorOperativa = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar("Configuración operativa actualizada correctamente.", TonoToast.Exito);
        }
        catch (ValidationException ex)
        {
            _erroresCampoOperativa = ex.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.First().ErrorMessage);
        }
        catch (Exception)
        {
            _mensajeErrorOperativa = "No pudimos guardar los cambios. Intenta nuevamente en unos segundos.";
        }
        finally
        {
            _guardandoOperativa = false;
        }
    }

    private async Task GuardarPresupuestoIaAsync()
    {
        _guardandoPresupuestoIa = true;
        _mensajeErrorPresupuestoIa = null;
        StateHasChanged();

        try
        {
            // Campo vacío = sin presupuesto configurado (desactiva el aviso), no "0 USD".
            decimal? presupuesto = null;
            if (!string.IsNullOrWhiteSpace(_presupuestoMensualIaUsd))
            {
                if (!decimal.TryParse(_presupuestoMensualIaUsd, out var valor))
                {
                    _mensajeErrorPresupuestoIa = "El presupuesto debe ser un número.";
                    return;
                }

                presupuesto = valor;
            }

            var resultado = await Mediator.Send(new ActualizarPresupuestoIaCommand(presupuesto));

            if (resultado.EsFallido)
            {
                _mensajeErrorPresupuestoIa = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar("Presupuesto de IA actualizado correctamente.", TonoToast.Exito);
        }
        catch (Exception)
        {
            _mensajeErrorPresupuestoIa = "No pudimos guardar los cambios. Intenta nuevamente en unos segundos.";
        }
        finally
        {
            _guardandoPresupuestoIa = false;
        }
    }

    private string? ObtenerError(string campo) => _erroresCampo.GetValueOrDefault(campo);

    private string? ObtenerErrorOperativa(string campo) => _erroresCampoOperativa.GetValueOrDefault(campo);
}
