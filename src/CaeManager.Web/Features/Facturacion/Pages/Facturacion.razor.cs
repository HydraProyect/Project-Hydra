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

    internal const string PestanaTarifas = "tarifas";
    internal const string PestanaResumen = "resumen";

    private static readonly IReadOnlyList<PestanaDefinicion> Pestanas =
    [
        new(PestanaTarifas, "Tarifas configuradas"),
        new(PestanaResumen, "Resumen mensual"),
    ];

    private static readonly int TotalConceptos = Enum.GetValues<ConceptoFacturable>().Length;

    private bool _cargando = true;
    private bool _errorCarga;
    private bool _cargandoTarifas;
    private bool _errorTarifas;
    private bool _guardando;

    private IReadOnlyList<ClienteSelectorDto> _clientes = [];
    private Guid _clienteSeleccionadoId = Guid.Empty;
    private List<TarifaClienteDto> _tarifas = [];
    private List<ConceptoFacturable> _conceptosDisponibles = [];

    private string _pestanaActiva = PestanaTarifas;

    // Cada carga que escribe estado tras un await lleva su número de solicitud.
    // Si al volver ya no es la última (cambió el cliente, el periodo, o se
    // lanzó otra carga igual), la respuesta se descarta: sin esto, las
    // tarifas de un cliente elegido antes y resuelto después se pintaban bajo
    // el cliente que el usuario tiene seleccionado ahora.
    private int _solicitudTarifas;
    private int _solicitudEstimado;
    private int _solicitudResumen;

    // Estimado del mes en curso, junto al selector de cliente.
    private ResumenFacturacionDto? _estimado;
    private bool _cargandoEstimado;
    private bool _errorEstimado;

    // Resumen mensual. El periodo que se pinta es el que se CALCULÓ, no el que
    // tienen ahora los filtros: cambiar el mes sin pulsar «Calcular» dejaba el
    // título y el enlace de exportación hablando de un mes cuyos datos no
    // estaban en pantalla.
    private int _anyoResumen = DateTime.Today.Year;
    private int _mesResumen = DateTime.Today.Month;
    private bool _cargandoResumen;
    private bool _errorResumen;
    private bool _resumenConsultado;
    private ResumenFacturacionDto? _resumen;
    private (Guid ClienteId, int Anyo, int Mes) _periodoResumen;

    // Formulario nueva tarifa
    private bool _mostrarFormularioNueva;
    private ConceptoFacturable _nuevaConcepto;
    private decimal _nuevaPrecio;
    private string _nuevaMoneda = "EUR";
    private string? _nuevaError;
    private Dictionary<string, string> _nuevaErrores = new();

    // Edición inline
    private Guid _tarifaEditandoId = Guid.Empty;
    // Versión de la tarifa tal como se abrió: vuelve en el Command para
    // detectar que otra persona guardó mientras el formulario estaba abierto.
    private Guid _versionEditando;
    private decimal _editPrecio;
    private string _editMoneda = "EUR";
    // Fallo del comando (conflicto de versión, tarifa inexistente) o excepción.
    private string? _editError;
    // Errores de validación por propiedad del comando. Antes se guardaban aquí
    // con la clave "PrecioUnitario"/"MonedaIso" pero la vista solo leía
    // "precio": un precio negativo no mostraba nada al pulsar Guardar.
    private Dictionary<string, string> _editErrores = new();

    // Eliminar tarifa pendiente de confirmar (antes iba directo del enlace al comando).
    private TarifaClienteDto? _tarifaAEliminar;
    private bool _confirmarEliminarVisible;
    private bool _eliminando;

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
        _conceptosDisponibles = [];
        _pestanaActiva = PestanaTarifas;
        _mostrarFormularioNueva = false;
        CancelarEdicion();

        _estimado = null;
        _errorEstimado = false;
        _cargandoEstimado = false;

        _resumen = null;
        _resumenConsultado = false;
        _errorResumen = false;
        _cargandoResumen = false;

        // Invalida lo que siguiera en vuelo para el cliente anterior, también
        // cuando se vuelve a «— Selecciona un cliente —».
        _solicitudTarifas++;
        _solicitudEstimado++;
        _solicitudResumen++;

        if (_clienteSeleccionadoId != Guid.Empty)
            await CargarDatosClienteAsync();
    }

    /// <summary>
    /// Tarifas y, después, el estimado del mes en curso. En serie y no a la
    /// vez: las dos consultas pasan por el mismo circuito.
    /// </summary>
    private async Task CargarDatosClienteAsync()
    {
        var clienteId = _clienteSeleccionadoId;
        await CargarTarifasAsync();

        if (clienteId == _clienteSeleccionadoId)
            await CargarEstimadoAsync();
    }

    private async Task CargarTarifasAsync()
    {
        var solicitud = ++_solicitudTarifas;
        var clienteId = _clienteSeleccionadoId;
        _cargandoTarifas = true;
        _errorTarifas = false;
        StateHasChanged();

        try
        {
            var tarifas = await Mediator.Send(new ObtenerTarifasClienteQuery(clienteId));
            if (solicitud != _solicitudTarifas) return;

            _tarifas = tarifas;
            RecalcularConceptosDisponibles();
        }
        catch (Exception)
        {
            if (solicitud != _solicitudTarifas) return;
            _errorTarifas = true;
        }
        finally
        {
            if (solicitud == _solicitudTarifas)
                _cargandoTarifas = false;
        }
    }

    /// <summary>
    /// El «Estimado de {mes}» del mockup: la misma consulta que el resumen
    /// mensual, para el mes en curso. No hay cifra nueva que calcular aquí;
    /// solo se decide cómo pintarla (ver <see cref="TextoEstimado"/>).
    /// </summary>
    private async Task CargarEstimadoAsync()
    {
        var solicitud = ++_solicitudEstimado;
        var clienteId = _clienteSeleccionadoId;
        var hoy = DateTime.Today;
        _cargandoEstimado = true;
        _errorEstimado = false;
        StateHasChanged();

        try
        {
            var estimado = await Mediator.Send(new ObtenerResumenFacturacionQuery(clienteId, hoy.Year, hoy.Month));
            if (solicitud != _solicitudEstimado) return;

            _estimado = estimado;
        }
        catch (Exception)
        {
            if (solicitud != _solicitudEstimado) return;
            _estimado = null;
            _errorEstimado = true;
        }
        finally
        {
            if (solicitud == _solicitudEstimado)
                _cargandoEstimado = false;
        }
    }

    private async Task CargarResumenAsync()
    {
        var solicitud = ++_solicitudResumen;
        var periodo = (_clienteSeleccionadoId, _anyoResumen, _mesResumen);
        _cargandoResumen = true;
        _errorResumen = false;
        StateHasChanged();

        try
        {
            var resumen = await Mediator.Send(
                new ObtenerResumenFacturacionQuery(periodo.Item1, periodo.Item2, periodo.Item3));
            if (solicitud != _solicitudResumen) return;

            _resumen = resumen;
            _periodoResumen = periodo;
            _resumenConsultado = true;
        }
        catch (Exception)
        {
            // Antes no había catch: una excepción de la consulta subía sin
            // aviso. Ahora se dice y se ofrece reintentar.
            if (solicitud != _solicitudResumen) return;
            _resumen = null;
            _resumenConsultado = false;
            _errorResumen = true;
        }
        finally
        {
            if (solicitud == _solicitudResumen)
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
            await CargarDatosClienteAsync();
        }
        catch (ValidationException ex)
        {
            _nuevaErrores = ErroresPorPropiedad(ex);
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
        _versionEditando = tarifa.Version;
        _editPrecio = tarifa.PrecioUnitario;
        _editMoneda = tarifa.MonedaIso;
        _editError = null;
        _editErrores = new();
    }

    private void CancelarEdicion()
    {
        _tarifaEditandoId = Guid.Empty;
        _editError = null;
        _editErrores = new();
    }

    private async Task GuardarEdicionAsync()
    {
        _editError = null;
        _editErrores = new();
        _guardando = true;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(
                new ActualizarTarifaClienteCommand(_tarifaEditandoId, _editPrecio, _editMoneda, _versionEditando));

            if (resultado.EsFallido)
            {
                _editError = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar("Tarifa actualizada correctamente.", TonoToast.Exito);
            _tarifaEditandoId = Guid.Empty;
            await CargarDatosClienteAsync();
        }
        catch (ValidationException ex)
        {
            _editErrores = ErroresPorPropiedad(ex);
        }
        catch (Exception)
        {
            _editError = "No pudimos guardar los cambios. Intenta nuevamente.";
        }
        finally
        {
            _guardando = false;
        }
    }

    private void AbrirConfirmarEliminar(TarifaClienteDto tarifa)
    {
        _tarifaAEliminar = tarifa;
        _confirmarEliminarVisible = true;
    }

    private async Task ConfirmarEliminarAsync()
    {
        if (_tarifaAEliminar is not { } tarifa)
            return;

        _eliminando = true;
        try
        {
            var resultado = await Mediator.Send(new EliminarTarifaClienteCommand(tarifa.Id));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            ToastService.Mostrar("Tarifa eliminada.", TonoToast.Exito);
            _confirmarEliminarVisible = false;
            _tarifaAEliminar = null;
            await CargarDatosClienteAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar la tarifa. Intenta nuevamente.", TonoToast.Error);
        }
        finally
        {
            _eliminando = false;
        }
    }

    private void CambiarPestana(string pestana)
    {
        _pestanaActiva = pestana;
        CancelarEdicion();
        _mostrarFormularioNueva = false;
    }

    private void CambiarMesResumen(string valor)
    {
        if (int.TryParse(valor, out var mes) && mes is >= 1 and <= 12)
            _mesResumen = mes;
    }

    private void CambiarConceptoNueva(string valor)
    {
        if (Enum.TryParse<ConceptoFacturable>(valor, out var concepto))
            _nuevaConcepto = concepto;
    }

    private void RecalcularConceptosDisponibles()
    {
        var usados = _tarifas.Select(t => t.Concepto).ToHashSet();
        _conceptosDisponibles = Enum.GetValues<ConceptoFacturable>()
            .Where(c => !usados.Contains(c))
            .ToList();

        if (_conceptosDisponibles.Count > 0)
            _nuevaConcepto = _conceptosDisponibles[0];
    }

    private static Dictionary<string, string> ErroresPorPropiedad(ValidationException ex) =>
        ex.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(g => g.Key, g => g.First().ErrorMessage);

    private string? ErrorEdicion(string propiedad) =>
        _editErrores.TryGetValue(propiedad, out var error) ? error : null;

    private string TextoConceptosTarificados =>
        _cargandoTarifas || _errorTarifas ? "—" : $"{_tarifas.Count} de {TotalConceptos}";

    private static string EtiquetaEstimado => $"Estimado de {NombreMes(DateTime.Today.Month).ToLowerInvariant()}";

    /// <summary>
    /// El total del mes en curso, sin inventar nada: sin tarifas no hay importe
    /// (el DTO trae la lista de totales vacía), y con tarifas en varias monedas
    /// se muestra el total por moneda que ya calculó el handler — la pantalla
    /// no vuelve a sumar subtotales.
    /// </summary>
    private string TextoEstimado
    {
        get
        {
            if (_cargandoEstimado) return "…";
            if (_errorEstimado) return "No disponible";
            if (_estimado is null || _estimado.Lineas.Count == 0) return "—";

            return string.Join(" · ", _estimado.TotalesPorMoneda.Select(t => FormatearImporte(t.Total, t.MonedaIso)));
        }
    }

    private string PistaEstimado =>
        _estimado is { Lineas.Count: 0 } && !_cargandoEstimado && !_errorEstimado
            ? "Sin tarifas configuradas"
            : "Mes en curso, con los datos de hoy";

    /// <summary>
    /// Cultura de la aplicación (es-ES, fijada en Program.cs), no de la
    /// pantalla: «857,40 EUR».
    /// </summary>
    internal static string FormatearImporte(decimal importe, string moneda) => $"{importe:N2} {moneda}";

    /// <summary>
    /// Ancho de la barra de «Dónde está el importe» respecto del subtotal
    /// mayor. Solo tiene sentido con una sola moneda y algún importe positivo:
    /// la vista no pinta el gráfico en otro caso.
    /// </summary>
    internal static decimal PorcentajeBarra(decimal subtotal, decimal maximo) =>
        maximo <= 0 ? 0 : Math.Round(subtotal / maximo * 100m, 1);

    // Invariante porque es CSS («50.5%», nunca «50,5%»), y "0.#" porque
    // Math.Round sobre decimal conserva la escala: sin él salía «50.0%».
    private static string AnchoBarra(decimal subtotal, decimal maximo) =>
        PorcentajeBarra(subtotal, maximo).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "%";

    private string UrlExportacion =>
        $"/facturacion/resumen.xlsx?clienteId={_periodoResumen.ClienteId}&anyo={_periodoResumen.Anyo}&mes={_periodoResumen.Mes}";

    /// <summary>
    /// Mismos nombres que <c>ObtenerTarifasClienteQueryHandler.NombreConcepto</c>
    /// (Application). Antes esta copia solo cubría 4 de los 7 conceptos y el
    /// desplegable de «Nueva tarifa» enseñaba «GestionProyectoRealizada» en
    /// crudo mientras la tabla decía «Gestión de proyecto realizada».
    /// </summary>
    internal static string NombreConcepto(ConceptoFacturable concepto) => concepto switch
    {
        ConceptoFacturable.TrabajadorActivo => "Trabajador activo",
        ConceptoFacturable.AltaCentro => "Alta de centro",
        ConceptoFacturable.VisitaTrabajadorExtranjero => "Visita de trabajador extranjero",
        ConceptoFacturable.DocumentoGestionado => "Documento gestionado",
        ConceptoFacturable.TecnicoAsignadoProyecto => "Técnico asignado a proyecto",
        ConceptoFacturable.GestionProyectoRealizada => "Gestión de proyecto realizada",
        ConceptoFacturable.DiaProyectoAbierto => "Día de proyecto abierto",
        _ => concepto.ToString()
    };

    /// <summary>
    /// Qué cuenta cada unidad, leído de lo que realmente cuenta
    /// <c>ObtenerResumenFacturacionQueryHandler</c> — no del comentario del
    /// enum, que para TrabajadorActivo dice «al cierre del período» cuando la
    /// consulta cuenta cualquier asignación viva en algún momento del mes.
    /// Si cambia una de esas consultas, esta frase cambia con ella.
    /// </summary>
    internal static string UnidadConcepto(ConceptoFacturable concepto) => concepto switch
    {
        ConceptoFacturable.TrabajadorActivo => "Por trabajador con asignación activa en algún momento del período",
        ConceptoFacturable.AltaCentro => "Por centro dado de alta durante el período",
        ConceptoFacturable.VisitaTrabajadorExtranjero => "Por trabajador sin DNI español que visita un centro en el período",
        ConceptoFacturable.DocumentoGestionado => "Por documento creado en el período para un trabajador asignado",
        ConceptoFacturable.TecnicoAsignadoProyecto => "Por técnico distinto asignado a algún proyecto en el período",
        ConceptoFacturable.GestionProyectoRealizada => "Por documento creado en el período en un proyecto",
        ConceptoFacturable.DiaProyectoAbierto => "Por día abierto de cada proyecto, contando el primero y el último",
        _ => string.Empty
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
