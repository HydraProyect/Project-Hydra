using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Proyectos.Commands.ActualizarProyecto;
using CaeManager.Application.Proyectos.Commands.AsignarTecnicoProyecto;
using CaeManager.Application.Proyectos.Commands.CerrarProyecto;
using CaeManager.Application.Proyectos.Commands.CrearProyecto;
using CaeManager.Application.Proyectos.Commands.DesasignarTecnicoProyecto;
using CaeManager.Application.Proyectos.Commands.EliminarProyecto;
using CaeManager.Application.Proyectos.Queries.ObtenerProyectoPorId;
using CaeManager.Application.Proyectos.Queries.ObtenerProyectos;
using CaeManager.Application.Proyectos.Queries.ObtenerTecnicosProyecto;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Web.Components.DesignSystem;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Proyectos.Pages;

public partial class Proyectos : ComponentBase
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;

    private bool _cargando = true;
    private bool _errorCarga;
    private bool _cargandoProyectos;

    private IReadOnlyList<ClienteSelectorDto> _clientes = [];
    private Guid _clienteSeleccionadoId = Guid.Empty;
    private IReadOnlyList<CentroSelectorDto> _centrosDisponibles = [];
    private List<ProyectoListaDto> _proyectos = [];

    private string _pestanaDetalle = "informacion";

    protected override Task OnInitializedAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            _clientes = await Mediator.Send(new ObtenerClientesParaSelectorQuery());
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

    private Task OnClienteSeleccionadoAsync(string valor)
    {
        _clienteSeleccionadoId = Guid.TryParse(valor, out var id) ? id : Guid.Empty;
        return OnClienteChangedAsync();
    }

    private async Task OnClienteChangedAsync()
    {
        _proyectos = [];
        _centrosDisponibles = [];
        CerrarDetalle();

        if (_clienteSeleccionadoId == Guid.Empty)
            return;

        _centrosDisponibles = await Mediator.Send(new ObtenerCentrosParaSelectorQuery(ClienteId: _clienteSeleccionadoId));
        await CargarProyectosAsync();
    }

    private async Task CargarProyectosAsync()
    {
        _cargandoProyectos = true;
        StateHasChanged();

        try
        {
            _proyectos = (await Mediator.Send(new ObtenerProyectosQuery(_clienteSeleccionadoId))).ToList();
        }
        finally
        {
            _cargandoProyectos = false;
        }
    }

    // ---- Nuevo proyecto (Drawer) ----

    private bool _drawerVisible;
    private string _nuevoCentroId = string.Empty;
    private string _nuevoNombre = string.Empty;
    private string _nuevaFechaInicio = string.Empty;
    private string _nuevaFechaFinPrevista = string.Empty;
    private string _nuevasNotas = string.Empty;
    private bool _guardando;
    private string? _mensajeErrorFormulario;
    private Dictionary<string, string> _erroresCampo = new();

    private void AbrirNuevoProyecto()
    {
        _nuevoCentroId = string.Empty;
        _nuevoNombre = string.Empty;
        _nuevaFechaInicio = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        _nuevaFechaFinPrevista = string.Empty;
        _nuevasNotas = string.Empty;
        _mensajeErrorFormulario = null;
        _erroresCampo = new Dictionary<string, string>();
        _drawerVisible = true;
    }

    private Task CerrarDrawerAsync(bool visible)
    {
        _drawerVisible = visible;
        return Task.CompletedTask;
    }

    private async Task CrearProyectoAsync()
    {
        _guardando = true;
        _mensajeErrorFormulario = null;
        _erroresCampo = new Dictionary<string, string>();
        StateHasChanged();

        try
        {
            if (!Guid.TryParse(_nuevoCentroId, out var centroId))
            {
                _mensajeErrorFormulario = "Selecciona un centro.";
                return;
            }

            if (!DateOnly.TryParse(_nuevaFechaInicio, out var fechaInicio))
            {
                _mensajeErrorFormulario = "Introduce una fecha de inicio válida.";
                return;
            }

            DateOnly? fechaFinPrevista = DateOnly.TryParse(_nuevaFechaFinPrevista, out var fv) ? fv : null;
            var notas = string.IsNullOrWhiteSpace(_nuevasNotas) ? null : _nuevasNotas;

            var resultado = await Mediator.Send(new CrearProyectoCommand(
                _clienteSeleccionadoId, centroId, _nuevoNombre, fechaInicio, fechaFinPrevista, notas));

            if (resultado.EsFallido)
            {
                _mensajeErrorFormulario = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar("Proyecto creado correctamente.", TonoToast.Exito);
            _drawerVisible = false;
            await CargarProyectosAsync();
        }
        catch (ValidationException ex)
        {
            _erroresCampo = ex.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.First().ErrorMessage);
        }
        catch (Exception)
        {
            _mensajeErrorFormulario = "No pudimos crear el proyecto. Intenta nuevamente en unos segundos.";
        }
        finally
        {
            _guardando = false;
        }
    }

    // ---- Detalle de proyecto (Información + Técnicos) ----

    private Guid? _proyectoSeleccionadoId;
    private ProyectoDetalleDto? _detalle;
    private bool _cargandoDetalle;

    private async Task SeleccionarProyectoAsync(Guid id)
    {
        _proyectoSeleccionadoId = id;
        _pestanaDetalle = "informacion";
        _cargandoDetalle = true;
        _detalle = null;
        _tecnicos = [];
        StateHasChanged();

        try
        {
            _detalle = await Mediator.Send(new ObtenerProyectoPorIdQuery(id));
            if (_detalle is not null)
            {
                _editNombre = _detalle.Nombre;
                _editFechaFinPrevista = _detalle.FechaFinPrevista?.ToString("yyyy-MM-dd") ?? string.Empty;
                _editNotas = _detalle.Notas ?? string.Empty;
            }
        }
        finally
        {
            _cargandoDetalle = false;
        }
    }

    private void CerrarDetalle()
    {
        _proyectoSeleccionadoId = null;
        _detalle = null;
        _editandoInfo = false;
        _mostrarCerrarConfirm = false;
    }

    private Task CambiarPestanaDetalleAsync(string pestana)
    {
        _pestanaDetalle = pestana;

        if (pestana == "tecnicos" && _detalle is not null && _tecnicos.Count == 0 && !_cargandoTecnicos)
            return CargarTecnicosAsync();

        return Task.CompletedTask;
    }

    // ---- Editar información ----

    private bool _editandoInfo;
    private string _editNombre = string.Empty;
    private string _editFechaFinPrevista = string.Empty;
    private string _editNotas = string.Empty;
    private Dictionary<string, string> _editErrores = new();
    private string? _editError;

    private void IniciarEdicionInfo() => _editandoInfo = true;

    private void CancelarEdicionInfo()
    {
        _editandoInfo = false;
        _editErrores = new();
        _editError = null;

        if (_detalle is not null)
        {
            _editNombre = _detalle.Nombre;
            _editFechaFinPrevista = _detalle.FechaFinPrevista?.ToString("yyyy-MM-dd") ?? string.Empty;
            _editNotas = _detalle.Notas ?? string.Empty;
        }
    }

    private async Task GuardarEdicionInfoAsync()
    {
        if (_detalle is null) return;

        _editErrores = new();
        _editError = null;
        _guardando = true;
        StateHasChanged();

        try
        {
            DateOnly? fechaFinPrevista = DateOnly.TryParse(_editFechaFinPrevista, out var fv) ? fv : null;
            var notas = string.IsNullOrWhiteSpace(_editNotas) ? null : _editNotas;

            var resultado = await Mediator.Send(new ActualizarProyectoCommand(_detalle.Id, _editNombre, fechaFinPrevista, notas));

            if (resultado.EsFallido)
            {
                _editError = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar("Proyecto actualizado correctamente.", TonoToast.Exito);
            _editandoInfo = false;
            await SeleccionarProyectoAsync(_detalle.Id);
            await CargarProyectosAsync();
        }
        catch (ValidationException ex)
        {
            _editErrores = ex.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.First().ErrorMessage);
        }
        catch (Exception)
        {
            _editError = "No pudimos guardar los cambios. Intenta nuevamente en unos segundos.";
        }
        finally
        {
            _guardando = false;
        }
    }

    // ---- Cerrar proyecto ----

    private bool _mostrarCerrarConfirm;
    private string _fechaCierre = string.Empty;
    private string? _errorCierre;

    private void AbrirCerrarConfirm(Guid id)
    {
        _proyectoSeleccionadoId = id;
        _fechaCierre = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        _errorCierre = null;
        _mostrarCerrarConfirm = true;
    }

    private async Task ConfirmarCerrarAsync()
    {
        if (_proyectoSeleccionadoId is null) return;

        if (!DateOnly.TryParse(_fechaCierre, out var fechaCierre))
        {
            _errorCierre = "Introduce una fecha de cierre válida.";
            return;
        }

        _guardando = true;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new CerrarProyectoCommand(_proyectoSeleccionadoId.Value, fechaCierre));

            if (resultado.EsFallido)
            {
                _errorCierre = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar("Proyecto cerrado correctamente.", TonoToast.Exito);
            _mostrarCerrarConfirm = false;
            await CargarProyectosAsync();

            if (_detalle is not null)
                await SeleccionarProyectoAsync(_detalle.Id);
        }
        finally
        {
            _guardando = false;
        }
    }

    // ---- Eliminar proyecto ----

    private bool _confirmarEliminarVisible;
    private Guid _idAEliminar;
    private string _nombreAEliminar = string.Empty;
    private bool _eliminando;

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
            var resultado = await Mediator.Send(new EliminarProyectoCommand(_idAEliminar));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            ToastService.Mostrar("Proyecto eliminado.", TonoToast.Exito);
            _confirmarEliminarVisible = false;

            if (_proyectoSeleccionadoId == _idAEliminar)
                CerrarDetalle();

            await CargarProyectosAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar el proyecto. Intenta nuevamente en unos segundos.", TonoToast.Error);
        }
        finally
        {
            _eliminando = false;
        }
    }

    // ---- Técnicos ----

    private List<TecnicoProyectoDto> _tecnicos = [];
    private bool _cargandoTecnicos;
    private IReadOnlyList<TrabajadorSelectorDto> _trabajadoresDisponibles = [];
    private bool _mostrarFormularioTecnico;
    private string _nuevoTecnicoTrabajadorId = string.Empty;
    private string _nuevoTecnicoFechaAlta = string.Empty;
    private string? _errorTecnico;

    private async Task CargarTecnicosAsync()
    {
        if (_detalle is null) return;

        _cargandoTecnicos = true;
        StateHasChanged();

        try
        {
            _tecnicos = (await Mediator.Send(new ObtenerTecnicosProyectoQuery(_detalle.Id))).ToList();
        }
        finally
        {
            _cargandoTecnicos = false;
        }
    }

    private async Task AbrirFormularioTecnicoAsync()
    {
        if (_trabajadoresDisponibles.Count == 0)
            _trabajadoresDisponibles = await Mediator.Send(new ObtenerTrabajadoresParaSelectorQuery());

        _nuevoTecnicoTrabajadorId = string.Empty;
        _nuevoTecnicoFechaAlta = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        _errorTecnico = null;
        _mostrarFormularioTecnico = true;
    }

    private async Task AsignarTecnicoAsync()
    {
        if (_detalle is null) return;

        if (!Guid.TryParse(_nuevoTecnicoTrabajadorId, out var trabajadorId))
        {
            _errorTecnico = "Selecciona un técnico.";
            return;
        }

        if (!DateOnly.TryParse(_nuevoTecnicoFechaAlta, out var fechaAlta))
        {
            _errorTecnico = "Introduce una fecha de alta válida.";
            return;
        }

        _guardando = true;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new AsignarTecnicoProyectoCommand(_detalle.Id, trabajadorId, fechaAlta));

            if (resultado.EsFallido)
            {
                _errorTecnico = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar("Técnico asignado correctamente.", TonoToast.Exito);
            _mostrarFormularioTecnico = false;
            await CargarTecnicosAsync();
        }
        finally
        {
            _guardando = false;
        }
    }

    private async Task DesasignarTecnicoAsync(Guid id)
    {
        try
        {
            var resultado = await Mediator.Send(new DesasignarTecnicoProyectoCommand(id, DateOnly.FromDateTime(DateTime.UtcNow)));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            ToastService.Mostrar("Técnico dado de baja del proyecto.", TonoToast.Exito);
            await CargarTecnicosAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos dar de baja al técnico. Intenta nuevamente en unos segundos.", TonoToast.Error);
        }
    }
}
