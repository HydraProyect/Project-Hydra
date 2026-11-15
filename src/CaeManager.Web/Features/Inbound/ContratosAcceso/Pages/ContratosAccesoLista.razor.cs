using CaeManager.Application.Inbound.ContratosAcceso.Queries;
using CaeManager.Application.Inbound.ContratosAcceso.Queries.ObtenerContratosAcceso;
using CaeManager.Domain.Inbound.ContratosAcceso;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Inbound.ContratosAcceso.Recursos;
using CaeManager.Web.Features.Inbound.ContratosAcceso.Servicios;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.Inbound.ContratosAcceso.Pages;

/// <summary>
/// Listado de contratos de acceso (tarjeta 13 de Inbound). Una sola lectura (<see cref="ObtenerContratosAccesoQuery"/>, sin
/// paginar) y el resto en cliente: nombres con <see cref="ResolutorContratosAcceso"/>, filtros, franja de estados con sus
/// recuentos y paginación. La autoridad es la de la Query y la de cada Command; la pantalla no la repite.
/// </summary>
public partial class ContratosAccesoLista
{
    private const string PendienteVence = "vence";

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ResolutorContratosAcceso Resolutor { get; set; } = default!;
    [Inject] private IActividadDeContratoAcceso Actividad { get; set; } = default!;
    [Inject] private NavigationManager Navegacion { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;

    private IReadOnlyList<ContratoAccesoFila> _todas = [];
    private IReadOnlyDictionary<Guid, ActividadDeContrato> _actividad = new Dictionary<Guid, ActividadDeContrato>();
    private IReadOnlyList<ContratoAccesoFila> _visibles = [];
    private IReadOnlyDictionary<string, int> _recuentos = new Dictionary<string, int>();
    private IReadOnlyList<OpcionEstado> _opcionesCentro = [];
    private IReadOnlyList<OpcionEstado> _opcionesTipo = [];
    private IReadOnlyList<OpcionEstado> _opcionesPendiente = [];
    private IReadOnlyList<OpcionFranjaEstado> _opcionesEstado = [];
    private DateOnly _hoy = CaeManager.Domain.Common.DiaDeNegocio.Hoy();

    private bool _cargando = true;
    private bool _errorCarga;
    private string _busqueda = string.Empty;
    private string _centro = string.Empty;
    private string _tipo = string.Empty;
    private string _pendiente = string.Empty;
    private string? _estado;
    private int _numeroPagina = 1;
    private int _tamanoPagina = 20;

    private ContratoAccesoFila? _renovando;
    private bool _altaVisible;
    private ContratoAccesoFila? _origenAlta;

    private bool HayFiltrosActivos =>
        !string.IsNullOrWhiteSpace(_busqueda) || _centro.Length > 0 || _tipo.Length > 0 || _pendiente.Length > 0
        || !string.IsNullOrWhiteSpace(_estado);

    private int TotalPaginas => Math.Max(1, (int)Math.Ceiling(_visibles.Count / (double)_tamanoPagina));

    private IReadOnlyList<ContratoAccesoFila> Pagina =>
        _visibles.Skip((Math.Min(_numeroPagina, TotalPaginas) - 1) * _tamanoPagina).Take(_tamanoPagina).ToList();

    protected override async Task OnInitializedAsync()
    {
        _opcionesEstado = ContratoAccesoUi.Franja(Textos);
        _opcionesTipo = Enum.GetValues<TipoContratoAcceso>()
            .Select(t => new OpcionEstado(t.ToString(), Textos[ContratoAccesoUi.ClaveTipo(t)].Value)).ToList();
        _opcionesPendiente = [new OpcionEstado(PendienteVence, Textos["PendienteVence"].Value)];
        await CargarAsync();
    }

    private async Task CargarAsync()
    {
        _cargando = true;
        _errorCarga = false;
        try
        {
            _hoy = CaeManager.Domain.Common.DiaDeNegocio.Hoy();
            var contratos = await Mediator.Send(new ObtenerContratosAccesoQuery());
            _todas = await Resolutor.ResolverAsync(contratos);
            _actividad = await Actividad.ObtenerAsync(_todas.Select(f => f.Contrato.Id).ToList());
            _opcionesCentro = _todas
                .Where(f => f.CentroNombre is not null)
                .GroupBy(f => f.Contrato.CentroId)
                .Select(g => new OpcionEstado(g.Key.ToString(), g.First().CentroNombre!))
                .OrderBy(o => o.Texto, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            Recalcular();
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

    // Filtros, orden y recuentos. Los recuentos de la franja no aplican el filtro de estado: cada cifra dice cuántos
    // contratos quedarían al marcar solo ese estado.
    private void Recalcular()
    {
        var sinEstado = _todas.Where(CumpleBusqueda).Where(CumpleCentro).Where(CumpleTipo).Where(CumplePendiente).ToList();
        _recuentos = sinEstado.GroupBy(f => f.Contrato.Estado.ToString()).ToDictionary(g => g.Key, g => g.Count());

        var estados = SeleccionEstados.Separar(_estado);
        _visibles = sinEstado
            .Where(f => estados.Count == 0 || estados.Contains(f.Contrato.Estado.ToString()))
            .OrderBy(f => ContratoAccesoUi.RangoDeOrden(f.Contrato.Estado))
            .ThenBy(f => f.Contrato.FechaFin ?? DateOnly.MaxValue)
            .ThenBy(f => f.EmpresaNombre, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _numeroPagina = Math.Min(_numeroPagina, TotalPaginas);
    }

    private bool CumpleBusqueda(ContratoAccesoFila f) =>
        string.IsNullOrWhiteSpace(_busqueda)
        || Contiene(f.EmpresaNombre) || Contiene(f.EmpresaCif) || Contiene(f.CentroNombre);

    private bool Contiene(string? texto) =>
        texto?.Contains(_busqueda.Trim(), StringComparison.CurrentCultureIgnoreCase) == true;

    private bool CumpleCentro(ContratoAccesoFila f) => _centro.Length == 0 || f.Contrato.CentroId.ToString() == _centro;

    private bool CumpleTipo(ContratoAccesoFila f) => _tipo.Length == 0 || f.Contrato.Tipo?.ToString() == _tipo;

    // «Vence en 30 días»: contrato vivo cuyo fin cae entre hoy y dentro de DiasDeAvisoDeVencimiento días.
    private bool CumplePendiente(ContratoAccesoFila f) =>
        _pendiente != PendienteVence
        || (f.Contrato.Estado is EstadoContratoAcceso.Activo or EstadoContratoAcceso.Suspendido
            && f.Contrato.FechaFin is { } fin
            && fin >= _hoy && fin.DayNumber - _hoy.DayNumber <= ContratoAccesoUi.DiasDeAvisoDeVencimiento);

    private static ContratoAccesoVista Vista(ContratoAccesoDto c) => new(c.Estado, c.FechaInicio, c.FechaFin, c.FechaCierreReal);

    private string VigenciaDe(ContratoAccesoDto c) => ContratoAccesoUi.Vigencia(Vista(c), _hoy, Textos);

    private Task BuscarAsync(string texto)
    {
        _busqueda = texto ?? string.Empty;
        _numeroPagina = 1;
        Recalcular();
        return Task.CompletedTask;
    }

    private void CambiarCentro(string valor) => Cambiar(() => _centro = valor);

    private void CambiarTipo(string valor) => Cambiar(() => _tipo = valor);

    private void CambiarPendiente(string valor) => Cambiar(() => _pendiente = valor);

    private void CambiarEstado(string? valor) => Cambiar(() => _estado = valor);

    private void Cambiar(Action cambio)
    {
        cambio();
        _numeroPagina = 1;
        Recalcular();
    }

    private Task LimpiarFiltrosAsync()
    {
        _busqueda = string.Empty;
        _centro = _tipo = _pendiente = string.Empty;
        _estado = null;
        _numeroPagina = 1;
        Recalcular();
        return Task.CompletedTask;
    }

    private void CambiarPagina(int pagina) => _numeroPagina = pagina;

    private void CambiarTamanoPagina(int tamano)
    {
        _tamanoPagina = tamano;
        _numeroPagina = 1;
    }

    private void AbrirRenovar(ContratoAccesoFila fila) => _renovando = fila;

    private void CerrarRenovar(bool visible)
    {
        if (!visible)
            _renovando = null;
    }

    private async Task RenovadoAsync(DateOnly nuevaFecha)
    {
        ToastService.Mostrar(Textos["ToastRenovado", ContratoAccesoUi.Fecha(nuevaFecha)], TonoToast.Exito);
        _renovando = null;
        await CargarAsync();
    }

    private void AbrirAlta(ContratoAccesoFila? origen)
    {
        _origenAlta = origen;
        _altaVisible = true;
    }

    private void CerrarAlta(bool visible)
    {
        if (!visible)
            _altaVisible = false;
    }

    private async Task AltaCreadaAsync(Guid id)
    {
        _altaVisible = false;
        ToastService.Mostrar(Textos["ToastAlta"], TonoToast.Exito);
        await CargarAsync();
    }
}
