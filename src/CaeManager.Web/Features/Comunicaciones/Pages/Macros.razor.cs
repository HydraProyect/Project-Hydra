using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Comunicaciones.Commands.CrearMacro;
using CaeManager.Application.Comunicaciones.Commands.EditarMacro;
using CaeManager.Application.Comunicaciones.Commands.EliminarMacro;
using CaeManager.Application.Comunicaciones.Queries.ObtenerMacros;
using CaeManager.Infrastructure.Comunicaciones;
using CaeManager.Web.Components.DesignSystem;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Features.Comunicaciones.Pages;

public partial class Macros : CaeManager.Web.Components.PaginaIntegrableConfiguracionBase
{
    [Inject] private ILogger<Macros> Logger { get; set; } = default!;
    [Inject] private IOptions<ComunicacionesOptions> OpcionesComunicaciones { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    private string _clienteFiltro = string.Empty;
    private IReadOnlyList<ClienteSelectorDto> _clientesSelector = [];
    private IReadOnlyList<MacroListaDto> _macros = [];

    private bool _cargando = true;
    private bool _errorCarga;

    private bool _drawerVisible;
    private Guid? _editandoId;
    // Version del registro tal como se abrio: vuelve en el Command para
    // detectar que otra persona guardo mientras el formulario estaba abierto.
    private Guid _versionEditando;
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
        // Módulo congelado por defecto (ComunicacionesOptions, P2 #26 de
        // docs/business/MATURITY_REVIEW.md): sin ingesta real de Graph
        // detrás, se presenta como si la ruta no existiera en vez de
        // mostrar una bandeja que nadie va a alimentar de verdad.
        if (!OpcionesComunicaciones.Value.Activo)
        {
            NavigationManager.NavigateTo("/not-found");
            return;
        }

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
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al cargar las macros de respuesta.");
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
        _versionEditando = macro.Version;
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
                var resultado = await Mediator.Send(new EditarMacroCommand(_editandoId.Value, _titulo, _cuerpo, clienteId, _versionEditando));
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
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al guardar la macro {MacroId} (null si es alta).", _editandoId);
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
            // Guid.Empty no: atribuía el borrado a un usuario inexistente en
            // vez de fallar, y eso deja una pista falsa en la auditoría —
            // justo lo contrario de para qué existe (hallazgo N-13 de
            // INFORME-AUDITORIA-2.md).
            var usuarioId = await CurrentUserService.ObtenerUsuarioActualIdAsync();
            if (usuarioId is null)
            {
                ToastService.Mostrar("Tu sesión ha caducado. Vuelve a iniciar sesión.", TonoToast.Error);
                return;
            }

            var resultado = await Mediator.Send(new EliminarMacroCommand(_idAEliminar, usuarioId.Value));

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
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al eliminar la macro {MacroId}.", _idAEliminar);
            ToastService.Mostrar("No pudimos eliminar la macro. Intenta nuevamente en unos segundos.", TonoToast.Error);
        }
        finally
        {
            _eliminando = false;
        }
    }
}
