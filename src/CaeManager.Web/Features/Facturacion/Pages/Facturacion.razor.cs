using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Facturacion.Commands.ActualizarTarifaCliente;
using CaeManager.Application.Facturacion.Commands.CrearTarifaCliente;
using CaeManager.Application.Facturacion.Commands.EliminarTarifaCliente;
using CaeManager.Application.Facturacion.Queries.ObtenerResumenFacturacion;
using CaeManager.Application.Facturacion.Queries.ObtenerTarifasCliente;
using CaeManager.Domain.Facturacion;
using CaeManager.Web.Components.DesignSystem;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Facturacion.Pages;

public partial class Facturacion : ComponentBase
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;

    private bool _cargando = true;
    private bool _errorCarga;
    private bool _cargandoTarifas;
    private bool _errorTarifas;
    private bool _cargandoResumen;
    private bool _guardando;

    private IReadOnlyList<ClienteSelectorDto> _clientes = [];
    private Guid _clienteSeleccionadoId = Guid.Empty;
    private List<TarifaClienteDto> _tarifas = [];
    private List<ConceptoFacturable> _conceptosDisponibles = [];
    private ResumenFacturacionDto? _resumen;

    private int _pestanaActiva;
    private int _anyoResumen = DateTime.Today.Year;
    private int _mesResumen = DateTime.Today.Month;

    // Formulario nueva tarifa
    private bool _mostrarFormularioNueva;
    private ConceptoFacturable _nuevaConcepto;
    private decimal _nuevaPrecio;
    private string _nuevaMoneda = "EUR";
    private string? _nuevaError;
    private Dictionary<string, string> _nuevaErrores = new();

    // Edición inline
    private Guid _tarifaEditandoId = Guid.Empty;
    private decimal _editPrecio;
    private string _editMoneda = "EUR";
    private Dictionary<string, string> _editErrores = new();

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

    private async Task OnClienteChangedAsync()
    {
        _tarifas = [];
        _resumen = null;
        _pestanaActiva = 0;
        _mostrarFormularioNueva = false;
        CancelarEdicion();

        if (_clienteSeleccionadoId != Guid.Empty)
            await CargarTarifasAsync();
    }

    private async Task CargarTarifasAsync()
    {
        _cargandoTarifas = true;
        _errorTarifas = false;
        StateHasChanged();

        try
        {
            _tarifas = await Mediator.Send(new ObtenerTarifasClienteQuery(_clienteSeleccionadoId));
            RecalcularConceptosDisponibles();
        }
        catch (Exception)
        {
            _errorTarifas = true;
        }
        finally
        {
            _cargandoTarifas = false;
        }
    }

    private async Task CargarResumenAsync()
    {
        _cargandoResumen = true;
        _resumen = null;
        StateHasChanged();

        try
        {
            _resumen = await Mediator.Send(
                new ObtenerResumenFacturacionQuery(_clienteSeleccionadoId, _anyoResumen, _mesResumen));
        }
        finally
        {
            _cargandoResumen = false;
        }
    }

    private async Task CrearTarifaAsync()
    {
        _nuevaError = null;
        _nuevaErrores = new();
        _guardando = true;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(
                new CrearTarifaClienteCommand(_clienteSeleccionadoId, _nuevaConcepto, _nuevaPrecio, _nuevaMoneda));

            if (resultado.EsFallido)
            {
                _nuevaError = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar("Tarifa guardada correctamente.", TonoToast.Exito);
            _mostrarFormularioNueva = false;
            _nuevaPrecio = 0;
            _nuevaMoneda = "EUR";
            await CargarTarifasAsync();
        }
        catch (ValidationException ex)
        {
            _nuevaErrores = ex.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.First().ErrorMessage);
        }
        catch (Exception)
        {
            _nuevaError = "No pudimos guardar la tarifa. Intenta nuevamente.";
        }
        finally
        {
            _guardando = false;
        }
    }

    private void IniciarEdicion(TarifaClienteDto tarifa)
    {
        _tarifaEditandoId = tarifa.Id;
        _editPrecio = tarifa.PrecioUnitario;
        _editMoneda = tarifa.MonedaIso;
        _editErrores = new();
    }

    private void CancelarEdicion()
    {
        _tarifaEditandoId = Guid.Empty;
        _editErrores = new();
    }

    private async Task GuardarEdicionAsync()
    {
        _editErrores = new();
        _guardando = true;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(
                new ActualizarTarifaClienteCommand(_tarifaEditandoId, _editPrecio, _editMoneda));

            if (resultado.EsFallido)
            {
                _editErrores["precio"] = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar("Tarifa actualizada correctamente.", TonoToast.Exito);
            _tarifaEditandoId = Guid.Empty;
            await CargarTarifasAsync();
        }
        catch (ValidationException ex)
        {
            _editErrores = ex.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.First().ErrorMessage);
        }
        catch (Exception)
        {
            _editErrores["precio"] = "No pudimos guardar los cambios. Intenta nuevamente.";
        }
        finally
        {
            _guardando = false;
        }
    }

    private async Task EliminarAsync(Guid tarifaId)
    {
        try
        {
            var resultado = await Mediator.Send(new EliminarTarifaClienteCommand(tarifaId));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            ToastService.Mostrar("Tarifa eliminada.", TonoToast.Exito);
            await CargarTarifasAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar la tarifa. Intenta nuevamente.", TonoToast.Error);
        }
    }

    private void CambiarPestana(int pestana)
    {
        _pestanaActiva = pestana;
        CancelarEdicion();
        _mostrarFormularioNueva = false;
    }

    private string PestanaClase(int indice) =>
        indice == _pestanaActiva ? "pestana-btn pestana-activa" : "pestana-btn";

    private void RecalcularConceptosDisponibles()
    {
        var usados = _tarifas.Select(t => t.Concepto).ToHashSet();
        _conceptosDisponibles = Enum.GetValues<ConceptoFacturable>()
            .Where(c => !usados.Contains(c))
            .ToList();

        if (_conceptosDisponibles.Count > 0)
            _nuevaConcepto = _conceptosDisponibles[0];
    }

    internal static string NombreConcepto(ConceptoFacturable concepto) => concepto switch
    {
        ConceptoFacturable.TrabajadorActivo => "Trabajador activo (mensual)",
        ConceptoFacturable.AltaCentro => "Alta de centro",
        ConceptoFacturable.VisitaTrabajadorExtranjero => "Visita de trabajador extranjero",
        ConceptoFacturable.DocumentoGestionado => "Documento gestionado",
        _ => concepto.ToString()
    };

    internal static string NombreMes(int mes) => mes switch
    {
        1 => "Enero",
        2 => "Febrero",
        3 => "Marzo",
        4 => "Abril",
        5 => "Mayo",
        6 => "Junio",
        7 => "Julio",
        8 => "Agosto",
        9 => "Septiembre",
        10 => "Octubre",
        11 => "Noviembre",
        12 => "Diciembre",
        _ => mes.ToString()
    };
}
