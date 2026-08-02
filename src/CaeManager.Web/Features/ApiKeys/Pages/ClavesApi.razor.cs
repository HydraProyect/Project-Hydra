using CaeManager.Application.ApiKeys.Commands.GenerarClaveApi;
using CaeManager.Application.ApiKeys.Commands.RevocarClaveApi;
using CaeManager.Application.ApiKeys.Queries.ObtenerClavesApi;
using CaeManager.Application.Tenants.Queries.ObtenerDelegaciones;
using CaeManager.Web.Components.DesignSystem;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.ApiKeys.Pages;

/// <summary>
/// Gestión de claves de la API pública (P3-29). Solo Administrador, y solo
/// sobre delegaciones de soporte ya existentes — ningún tenant se
/// autogestiona esto en v1 (ver GenerarClaveApiCommand).
/// </summary>
public partial class ClavesApi : ComponentBase
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private ILogger<ClavesApi> Logger { get; set; } = default!;

    private IReadOnlyList<DelegacionDto> _delegacionesSoporte = [];
    private bool _cargandoDelegaciones = true;
    private Guid? _delegacionSeleccionadaId;

    private IReadOnlyList<ClaveApiDto> _claves = [];
    private bool _cargandoClaves;

    private bool _mostrarFormularioGenerar;
    private string _nombreNuevaClave = string.Empty;
    private bool _generando;
    private string? _errorGenerar;
    private string? _claveGenerada;

    private ClaveApiDto? _claveARevocar;
    private bool _revocando;
    private Guid? _procesandoId;

    protected override async Task OnInitializedAsync()
    {
        _cargandoDelegaciones = true;

        try
        {
            var delegaciones = await Mediator.Send(new ObtenerDelegacionesQuery());
            _delegacionesSoporte = delegaciones.Where(d => d.EsSoporte && d.SomosLaConsultora).ToList();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al cargar las delegaciones de soporte disponibles.");
            ToastService.Mostrar("No pudimos cargar los clientes disponibles.", TonoToast.Error);
        }
        finally
        {
            _cargandoDelegaciones = false;
        }
    }

    private async Task OnDelegacionSeleccionadaAsync(string valor)
    {
        _delegacionSeleccionadaId = Guid.TryParse(valor, out var id) ? id : null;
        await CargarClavesAsync();
    }

    private async Task CargarClavesAsync()
    {
        if (_delegacionSeleccionadaId is not { } delegacionId)
        {
            _claves = [];
            return;
        }

        _cargandoClaves = true;
        StateHasChanged();

        try
        {
            _claves = await Mediator.Send(new ObtenerClavesApiQuery(delegacionId));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al cargar las claves API de la delegación {DelegacionId}.", delegacionId);
            ToastService.Mostrar("No pudimos cargar las claves de este cliente.", TonoToast.Error);
        }
        finally
        {
            _cargandoClaves = false;
        }
    }

    private async Task GenerarAsync()
    {
        if (_delegacionSeleccionadaId is not { } delegacionId) return;

        _generando = true;
        _errorGenerar = null;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new GenerarClaveApiCommand(delegacionId, _nombreNuevaClave));

            if (resultado.EsFallido)
            {
                _errorGenerar = resultado.Error.Mensaje;
                return;
            }

            _mostrarFormularioGenerar = false;
            _nombreNuevaClave = string.Empty;
            _claveGenerada = resultado.Valor.ClaveEnClaro;
            await CargarClavesAsync();
        }
        catch (ValidationException ex)
        {
            _errorGenerar = string.Join(" ", ex.Errors.Select(e => e.ErrorMessage));
        }
        finally
        {
            _generando = false;
        }
    }

    private async Task RevocarAsync()
    {
        if (_claveARevocar is null || _delegacionSeleccionadaId is not { } delegacionId) return;

        _revocando = true;
        _procesandoId = _claveARevocar.Id;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new RevocarClaveApiCommand(delegacionId, _claveARevocar.Id));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            ToastService.Mostrar("Clave revocada.", TonoToast.Exito);
            _claveARevocar = null;
            await CargarClavesAsync();
        }
        finally
        {
            _revocando = false;
            _procesandoId = null;
        }
    }
}
