using CaeManager.Application.Tenants.Commands.CrearOperadorCaeExterno;
using CaeManager.Application.Tenants.Commands.CrearTenantPropietarioDeOperadorCaeExterno;
using CaeManager.Application.Tenants.Queries.ObtenerOperadoresCaeExternos;
using CaeManager.Web.Components.DesignSystem;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Delegaciones;

public partial class OperadoresCaeExternosPanel : ComponentBase, IDisposable
{
    private enum ModalActivo { Ninguno, Operador, TenantPropietario }

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private ILogger<OperadoresCaeExternosPanel> Logger { get; set; } = default!;

    private readonly CancellationTokenSource _ciclo = new();
    private IReadOnlyList<OperadorCaeExternoDto> _operadores = [];
    private bool _cargando = true;
    private bool _error;
    private bool _desechado;

    private ModalActivo _modal = ModalActivo.Ninguno;
    private OperadorCaeExternoDto? _operadorDestino;
    private string _nombre = string.Empty;
    private bool _enCurso;
    private string? _errorFormulario;

    private string TituloModal => _modal is ModalActivo.Operador ? "Nuevo Operador CAE externo" : "Nuevo Tenant propietario";
    private string EtiquetaNombre => _modal is ModalActivo.Operador ? "Nombre del Operador CAE externo" : "Nombre del Tenant propietario";
    private string PlaceholderNombre => _modal is ModalActivo.Operador ? "ArcoSPA" : "Laboratorios Dexter";

    protected override Task OnInitializedAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        _cargando = true;
        _error = false;
        try
        {
            var operadores = await Mediator.Send(new ObtenerOperadoresCaeExternosQuery(), _ciclo.Token);
            if (_desechado) return;
            _operadores = operadores;
        }
        catch (OperationCanceledException) when (_ciclo.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (_desechado) return;
            Logger.LogError(ex, "Error al cargar los Operadores CAE externos.");
            _error = true;
        }
        finally
        {
            if (!_desechado) _cargando = false;
        }
    }

    private void AbrirNuevoOperador() => AbrirModal(ModalActivo.Operador, null);

    private void AbrirNuevoTenant(OperadorCaeExternoDto operador) => AbrirModal(ModalActivo.TenantPropietario, operador);

    private void AbrirModal(ModalActivo modal, OperadorCaeExternoDto? operador)
    {
        if (_enCurso) return;
        _modal = modal;
        _operadorDestino = operador;
        _nombre = string.Empty;
        _errorFormulario = null;
    }

    private void CerrarModal(bool visible)
    {
        if (!visible && !_enCurso) _modal = ModalActivo.Ninguno;
    }

    private async Task ConfirmarAsync()
    {
        if (_enCurso || _modal is ModalActivo.Ninguno) return;

        _enCurso = true;
        _errorFormulario = null;
        StateHasChanged();
        try
        {
            var resultado = _modal is ModalActivo.Operador
                ? await Mediator.Send(new CrearOperadorCaeExternoCommand(_nombre))
                : await Mediator.Send(new CrearTenantPropietarioDeOperadorCaeExternoCommand(_operadorDestino!.TenantId, _nombre));
            if (_desechado) return;

            if (resultado.EsFallido)
            {
                _errorFormulario = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar(
                _modal is ModalActivo.Operador ? "Operador CAE externo creado." : "Tenant propietario creado y operado por el Operador.",
                TonoToast.Exito);
            _modal = ModalActivo.Ninguno;
            await CargarAsync();
        }
        catch (ValidationException ex)
        {
            if (!_desechado) _errorFormulario = string.Join(" ", ex.Errors.Select(e => e.ErrorMessage));
        }
        finally
        {
            if (!_desechado) _enCurso = false;
        }
    }

    public void Dispose()
    {
        _desechado = true;
        _ciclo.Cancel();
        _ciclo.Dispose();
    }
}
