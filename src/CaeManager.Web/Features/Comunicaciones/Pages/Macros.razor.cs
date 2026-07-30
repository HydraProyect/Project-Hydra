using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Comunicaciones.Commands.CrearMacro;
using CaeManager.Application.Comunicaciones.Commands.EditarMacro;
using CaeManager.Application.Comunicaciones.Commands.EliminarMacro;
using CaeManager.Application.Comunicaciones.Queries.ObtenerMacros;
using CaeManager.Web.Components.DesignSystem;
using FluentValidation;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Comunicaciones.Pages;

public partial class Macros : ComponentBase
{
    private string _clienteFiltro = string.Empty;
    private IReadOnlyList<ClienteSelectorDto> _clientesSelector = [];
    private IReadOnlyList<MacroListaDto> _macros = [];

    private bool _cargando = true;
    private bool _errorCarga;

    private bool _drawerVisible;
    private Guid? _editandoId;
    private string _titulo = string.Empty;
    private string _cuerpo = string.Empty;
    private string _clienteIdFormulario = string.Empty;
    private bool _guardando;
    private string? _mensajeErrorFormulario;
    private Dictionary<string, string> _erroresCampo = new();

    private bool _confirmarEliminarVisible;
    private Guid _idAEliminar;
    private string _tituloAEliminar = string.Empty;
    private bool _eliminando;

    protected override async Task OnInitializedAsync()
    {
        _clientesSelector = await Mediator.Send(new ObtenerClientesParaSelectorQuery());
        await CargarAsync();
    }

    private async Task CargarAsync()
    {
        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            var clienteId = Guid.TryParse(_clienteFiltro, out var id) ? id : (Guid?)null;
            _macros = await Mediator.Send(new ObtenerMacrosQuery(clienteId));
        }
        catch (Exception)
        {
            _errorCarga = true;
        }
        finally
        {
            _cargando = false;
            StateHasChanged();
        }
    }

    private Task FiltrarPorClienteAsync(string clienteId)
    {
        _clienteFiltro = clienteId;
        return CargarAsync();
    }

    private void AbrirCrear()
    {
        _editandoId = null;
        _titulo = string.Empty;
        _cuerpo = string.Empty;
        _clienteIdFormulario = string.Empty;
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
    }

    private void AbrirEditar(MacroListaDto macro)
    {
        _editandoId = macro.Id;
        _titulo = macro.Titulo;
        _cuerpo = macro.CuerpoHtml;
        _clienteIdFormulario = macro.ClienteId?.ToString() ?? string.Empty;
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
    }

    private Task CerrarDrawerAsync(bool visible)
    {
        _drawerVisible = visible;
        return Task.CompletedTask;
    }

    private async Task GuardarAsync()
    {
        _guardando = true;
        _mensajeErrorFormulario = null;
        _erroresCampo = new Dictionary<string, string>();

        try
        {
            var clienteId = Guid.TryParse(_clienteIdFormulario, out var id) ? id : (Guid?)null;

            string? mensajeError;
            if (_editandoId is null)
            {
                var resultado = await Mediator.Send(new CrearMacroCommand(_titulo, _cuerpo, clienteId));
                mensajeError = resultado.EsFallido ? resultado.Error.Mensaje : null;
            }
            else
            {
                var resultado = await Mediator.Send(new EditarMacroCommand(_editandoId.Value, _titulo, _cuerpo, clienteId));
                mensajeError = resultado.EsFallido ? resultado.Error.Mensaje : null;
            }

            if (mensajeError is not null)
            {
                _mensajeErrorFormulario = mensajeError;
                return;
            }

            ToastService.Mostrar(_editandoId is null ? "Macro creada correctamente." : "Macro actualizada correctamente.", TonoToast.Exito);
            _drawerVisible = false;
            await CargarAsync();
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

    private void AbrirEliminar(Guid id, string titulo)
    {
        _idAEliminar = id;
        _tituloAEliminar = titulo;
        _confirmarEliminarVisible = true;
    }

    private async Task ConfirmarEliminarAsync()
    {
        _eliminando = true;

        try
        {
            var usuarioId = await CurrentUserService.ObtenerUsuarioActualIdAsync();
            var resultado = await Mediator.Send(new EliminarMacroCommand(_idAEliminar, usuarioId ?? Guid.Empty));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            }
            else
            {
                ToastService.Mostrar("Macro eliminada correctamente.", TonoToast.Exito);
                _confirmarEliminarVisible = false;
                await CargarAsync();
            }
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar la macro. Intenta nuevamente en unos segundos.", TonoToast.Error);
        }
        finally
        {
            _eliminando = false;
        }
    }
}
