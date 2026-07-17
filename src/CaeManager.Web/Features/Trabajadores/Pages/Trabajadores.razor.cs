using CaeManager.Application.Trabajadores.Commands.CrearTrabajador;
using CaeManager.Application.Trabajadores.Commands.EditarTrabajador;
using CaeManager.Application.Trabajadores.Commands.EliminarTrabajador;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadorPorId;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratasParaSelector;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.QuickGrid;

namespace CaeManager.Web.Features.Trabajadores.Pages;

public partial class Trabajadores : ComponentBase
{
    private readonly PaginationState _paginacion = new() { ItemsPerPage = 20 };
    private QuickGrid<TrabajadorListaDto>? _grid;

    private string _busqueda = string.Empty;
    private string _filtroEmpresaId = string.Empty;
    private string _filtroSubcontrataId = string.Empty;
    private bool _cargando = true;
    private bool _errorCarga;
    private int _totalElementos;

    private IReadOnlyList<EmpresaSelectorDto> _empresasDisponibles = [];
    private IReadOnlyList<SubcontrataSelectorDto> _subcontratasDisponibles = [];

    private bool _drawerVisible;
    private Guid? _editandoId;
    private string _tipoEmpleador = "empresa";
    private string _empresaId = string.Empty;
    private string _subcontrataId = string.Empty;
    private string _empleadorNombreSoloLectura = string.Empty;
    private string _dni = string.Empty;
    private string _nombre = string.Empty;
    private string _apellidos = string.Empty;
    private string _fechaNacimiento = string.Empty;
    private string _email = string.Empty;
    private string _observaciones = string.Empty;
    private bool _guardando;
    private string? _mensajeErrorFormulario;
    private Dictionary<string, string> _erroresCampo = new();

    private bool _confirmarEliminarVisible;
    private Guid _idAEliminar;
    private string _nombreAEliminar = string.Empty;
    private bool _eliminando;

    [SupplyParameterFromQuery(Name = "q")]
    public string? TerminoBusquedaInicial { get; set; }

    protected override async Task OnInitializedAsync()
    {
        if (!string.IsNullOrWhiteSpace(TerminoBusquedaInicial))
            _busqueda = TerminoBusquedaInicial;

        _empresasDisponibles = await Mediator.Send(new ObtenerEmpresasParaSelectorQuery());
        _subcontratasDisponibles = await Mediator.Send(new ObtenerSubcontratasParaSelectorQuery());
    }

    private async ValueTask<GridItemsProviderResult<TrabajadorListaDto>> ProveerElementosAsync(
        GridItemsProviderRequest<TrabajadorListaDto> request)
    {
        _cargando = true;
        _errorCarga = false;

        try
        {
            var pagina = (request.StartIndex / _paginacion.ItemsPerPage) + 1;

            var resultado = await Mediator.Send(new ObtenerTrabajadoresQuery(
                Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
                EmpresaId: Guid.TryParse(_filtroEmpresaId, out var empresaId) ? empresaId : null,
                SubcontrataId: Guid.TryParse(_filtroSubcontrataId, out var subcontrataId) ? subcontrataId : null,
                Pagina: pagina,
                TamanoPagina: _paginacion.ItemsPerPage));

            _totalElementos = resultado.TotalElementos;

            return GridItemsProviderResult.From(resultado.Elementos.ToList(), resultado.TotalElementos);
        }
        catch (Exception)
        {
            _errorCarga = true;
            return GridItemsProviderResult.From(new List<TrabajadorListaDto>(), 0);
        }
        finally
        {
            _cargando = false;
            StateHasChanged();
        }
    }

    private async Task FiltrarPorEmpresaAsync(string valor)
    {
        _filtroEmpresaId = valor;
        _filtroSubcontrataId = string.Empty;
        await RecargarAsync();
    }

    private async Task FiltrarPorSubcontrataAsync(string valor)
    {
        _filtroSubcontrataId = valor;
        _filtroEmpresaId = string.Empty;
        await RecargarAsync();
    }

    private async Task BuscarAsync(string valor)
    {
        _busqueda = valor;
        await RecargarAsync();
    }

    private async Task RecargarAsync()
    {
        await _paginacion.SetCurrentPageIndexAsync(0);

        if (_grid is not null)
            await _grid.RefreshDataAsync();

        StateHasChanged();
    }

    private async Task AbrirCrearAsync()
    {
        _empresasDisponibles = await Mediator.Send(new ObtenerEmpresasParaSelectorQuery());
        _subcontratasDisponibles = await Mediator.Send(new ObtenerSubcontratasParaSelectorQuery());

        _editandoId = null;
        // Si la lista ya está filtrada por Empresa o Subcontrata, se presupone
        // que el trabajador que se va a dar de alta es de ese mismo empleador.
        if (!string.IsNullOrWhiteSpace(_filtroSubcontrataId))
        {
            _tipoEmpleador = "subcontrata";
            _subcontrataId = _filtroSubcontrataId;
            _empresaId = string.Empty;
        }
        else
        {
            _tipoEmpleador = "empresa";
            _empresaId = _filtroEmpresaId;
            _subcontrataId = string.Empty;
        }
        _dni = string.Empty;
        _nombre = string.Empty;
        _apellidos = string.Empty;
        _fechaNacimiento = string.Empty;
        _email = string.Empty;
        _observaciones = string.Empty;
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
    }

    private void SeleccionarTipoEmpresa() => CambiarTipoEmpleador("empresa");

    private void SeleccionarTipoSubcontrata() => CambiarTipoEmpleador("subcontrata");

    private void CambiarTipoEmpleador(string tipo)
    {
        _tipoEmpleador = tipo;
        _empresaId = string.Empty;
        _subcontrataId = string.Empty;
    }

    private async Task AbrirEditarAsync(Guid id)
    {
        var trabajador = await Mediator.Send(new ObtenerTrabajadorPorIdQuery(id));
        if (trabajador is null)
        {
            ToastService.Mostrar("No encontramos este trabajador. Puede que ya se haya eliminado.", TonoToast.Error);
            await RecargarAsync();
            return;
        }

        _editandoId = trabajador.Id;
        _tipoEmpleador = trabajador.SubcontrataId is not null ? "subcontrata" : "empresa";
        _empresaId = trabajador.EmpresaId?.ToString() ?? string.Empty;
        _subcontrataId = trabajador.SubcontrataId?.ToString() ?? string.Empty;
        _empleadorNombreSoloLectura = trabajador.EmpleadorNombre;
        _dni = trabajador.Dni;
        _nombre = trabajador.Nombre;
        _apellidos = trabajador.Apellidos;
        _fechaNacimiento = trabajador.FechaNacimiento?.ToString("yyyy-MM-dd") ?? string.Empty;
        _email = trabajador.Email ?? string.Empty;
        _observaciones = trabajador.Observaciones ?? string.Empty;
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
            var email = string.IsNullOrWhiteSpace(_email) ? null : _email;
            var observaciones = string.IsNullOrWhiteSpace(_observaciones) ? null : _observaciones;
            var fechaNacimiento = DateOnly.TryParse(_fechaNacimiento, out var fecha) ? fecha : (DateOnly?)null;

            string? mensajeError;

            if (_editandoId is null)
            {
                Guid? empresaId = null;
                Guid? subcontrataId = null;

                if (_tipoEmpleador == "empresa")
                {
                    if (!Guid.TryParse(_empresaId, out var empresaIdValor))
                    {
                        _mensajeErrorFormulario = "Selecciona una empresa.";
                        return;
                    }
                    empresaId = empresaIdValor;
                }
                else
                {
                    if (!Guid.TryParse(_subcontrataId, out var subcontrataIdValor))
                    {
                        _mensajeErrorFormulario = "Selecciona una subcontrata.";
                        return;
                    }
                    subcontrataId = subcontrataIdValor;
                }

                var resultado = await Mediator.Send(
                    new CrearTrabajadorCommand(empresaId, subcontrataId, _nombre, _apellidos, _dni, fechaNacimiento, email, observaciones));
                mensajeError = resultado.EsFallido ? resultado.Error.Mensaje : null;
            }
            else
            {
                var resultado = await Mediator.Send(
                    new EditarTrabajadorCommand(_editandoId.Value, _nombre, _apellidos, fechaNacimiento, email, observaciones));
                mensajeError = resultado.EsFallido ? resultado.Error.Mensaje : null;
            }

            if (mensajeError is not null)
            {
                _mensajeErrorFormulario = mensajeError;
                return;
            }

            ToastService.Mostrar(
                _editandoId is null ? "Trabajador creado correctamente." : "Trabajador actualizado correctamente.",
                TonoToast.Exito);

            _drawerVisible = false;
            await RecargarAsync();
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

    private string? PistaDni
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_dni)) return null;

            var resultado = ValidadorIdentificacion.Analizar(_dni);
            return resultado.Tipo switch
            {
                TipoIdentificacion.Dni => resultado.EsValido ? "✓ DNI con formato y dígito de control correctos." : "✗ Formato de DNI, pero el dígito de control no coincide.",
                TipoIdentificacion.Nie => resultado.EsValido ? "✓ NIE con formato y dígito de control correctos." : "✗ Formato de NIE, pero el dígito de control no coincide.",
                TipoIdentificacion.NifEmpresa => resultado.EsValido ? "✓ Formato de CIF válido — comprueba que sea la persona y no la empresa." : "✗ Parece un CIF, pero el dígito de control no coincide.",
                TipoIdentificacion.TieSoporte => "✓ Número de soporte TIE reconocido.",
                _ => "ℹ Documento no español (pasaporte u otro) — se acepta sin validar dígito de control."
            };
        }
    }

    private string ToneDni
    {
        get
        {
            var resultado = ValidadorIdentificacion.Analizar(_dni);
            if (resultado.Tipo is TipoIdentificacion.TieSoporte or TipoIdentificacion.Otros) return "info";
            return resultado.EsValido ? "exito" : "error";
        }
    }

    private static string NombreCompleto(TrabajadorListaDto trabajador) => $"{trabajador.Nombre} {trabajador.Apellidos}";

    private void AbrirEliminar(Guid id, string nombre)
    {
        _idAEliminar = id;
        _nombreAEliminar = nombre;
        _confirmarEliminarVisible = true;
    }

    private async Task ConfirmarEliminarAsync()
    {
        _eliminando = true;

        try
        {
            var usuarioId = await CurrentUserService.ObtenerUsuarioActualIdAsync();
            var resultado = await Mediator.Send(new EliminarTrabajadorCommand(_idAEliminar, usuarioId ?? Guid.Empty));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            }
            else
            {
                ToastService.Mostrar("Trabajador eliminado correctamente.", TonoToast.Exito);
                _confirmarEliminarVisible = false;
                await RecargarAsync();
            }
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar el trabajador. Intenta nuevamente en unos segundos.", TonoToast.Error);
        }
        finally
        {
            _eliminando = false;
        }
    }
}
