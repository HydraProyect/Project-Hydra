using CaeManager.Application.Inbound.Requisitos.Commands.RetirarPlantillaRequisitos;
using CaeManager.Application.Inbound.Requisitos.Queries;
using CaeManager.Domain.Inbound.Requisitos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Inbound.Plantillas.Recursos;
using CaeManager.Web.Features.Inbound.Plantillas.Servicios;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.Inbound.Plantillas.Pages;

/// <summary>Una plantilla del listado con el nombre de su Actividad, riesgo, Centro o Cliente empresarial (nulo si no se ve).</summary>
public sealed record FilaPlantilla(PlantillaRequisitosResumenDto Plantilla, string? Referencia);

/// <summary>
/// Listado de plantillas de requisitos (tarjeta 17 de Inbound) con el panel de requisitos de la elegida. Lecturas:
/// <see cref="ObtenerPlantillasRequisitosQuery"/> (paginada en el servidor, aquí se piden todas las páginas) y
/// <see cref="ObtenerPlantillaRequisitosPorIdQuery"/> al elegir. La autoridad es la de la Query y la de cada Command.
/// </summary>
public partial class PlantillasRequisitos
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ResolutorPlantillasRequisitos Resolutor { get; set; } = default!;
    [Inject] private IUsoDePlantillasRequisitos Uso { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;

    private IReadOnlyList<FilaPlantilla> _todas = [];
    private IReadOnlyList<FilaPlantilla> _visibles = [];
    private IReadOnlyDictionary<Guid, int> _usos = new Dictionary<Guid, int>();
    private IReadOnlyDictionary<string, int> _recuentos = new Dictionary<string, int>();
    private IReadOnlyList<OpcionFranjaEstado> _opcionesNivel = [];
    private ReferenciasPlantillas _refs = new(
        new Dictionary<Guid, string>(), new Dictionary<Guid, string>(), new Dictionary<Guid, string>(),
        new Dictionary<Guid, string>(), new Dictionary<Guid, Application.TiposDocumento.Queries.ObtenerTiposDocumento.TipoDocumentoListaDto>());

    private bool _cargando = true;
    private bool _errorCarga;
    private string _busqueda = string.Empty;
    private string? _niveles;
    private Guid? _seleccion;

    private PlantillaRequisitosDto? _detalle;
    private bool _cargandoDetalle;
    private bool _errorDetalle;

    private bool _altaVisible;
    private bool _retirando;
    private bool _enProgreso;
    private string? _errorRetirar;

    private bool HayFiltrosActivos => !string.IsNullOrWhiteSpace(_busqueda) || !string.IsNullOrWhiteSpace(_niveles);

    private FilaPlantilla? SeleccionVisible =>
        _visibles.FirstOrDefault(f => f.Plantilla.Id == _seleccion) ?? _visibles.FirstOrDefault();

    private IReadOnlyList<RequisitoPlantillaDto> RequisitosActivos =>
        _detalle?.Requisitos.Where(r => r.Activo).ToList() ?? [];

    protected override async Task OnInitializedAsync()
    {
        _opcionesNivel = PlantillaRequisitosUi.Niveles
            .Select(n => new OpcionFranjaEstado(Textos[PlantillaRequisitosUi.ClaveNivel(n)].Value, TonoBadge.Neutro, n.ToString()))
            .ToList();
        await CargarAsync();
    }

    private async Task CargarAsync()
    {
        _cargando = true;
        _errorCarga = false;
        try
        {
            var resumenes = new List<PlantillaRequisitosResumenDto>();
            for (var pagina = 1; ; pagina++)
            {
                var resultado = await Mediator.Send(new ObtenerPlantillasRequisitosQuery(Pagina: pagina, TamanoPagina: 100));
                resumenes.AddRange(resultado.Elementos);
                if (resultado.Elementos.Count == 0 || resumenes.Count >= resultado.TotalElementos)
                    break;
            }

            _refs = await Resolutor.CargarAsync();
            _todas = resumenes.Select(p => new FilaPlantilla(p, _refs.NombreDe(p.Tipo, p.ReferenciaId)))
                .OrderBy(f => f.Plantilla.Tipo)
                .ThenBy(f => NombreDe(f), StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            _usos = await Uso.ContratosPorPlantillaAsync(_todas.Select(f => f.Plantilla.Id).ToList());
            Recalcular();
            await CargarDetalleAsync();
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

    private void Recalcular()
    {
        var sinNivel = _todas.Where(CumpleBusqueda).ToList();
        _recuentos = sinNivel.GroupBy(f => f.Plantilla.Tipo.ToString()).ToDictionary(g => g.Key, g => g.Count());
        var niveles = SeleccionEstados.Separar(_niveles);
        _visibles = sinNivel.Where(f => niveles.Count == 0 || niveles.Contains(f.Plantilla.Tipo.ToString())).ToList();
    }

    private bool CumpleBusqueda(FilaPlantilla f) =>
        string.IsNullOrWhiteSpace(_busqueda)
        || NombreDe(f).Contains(_busqueda.Trim(), StringComparison.CurrentCultureIgnoreCase);

    // Las exigencias del Cliente empresarial se nombran por él; el resto, por su Actividad, riesgo o Centro.
    internal string NombreDe(FilaPlantilla f) => f.Plantilla.Tipo == TipoPlantillaRequisitos.ExigenciasClienteEmpresarial
        ? Textos["NombreExigencias", f.Referencia ?? Textos["ReferenciaNoDisponible"].Value].Value
        : f.Referencia ?? Textos["ReferenciaNoDisponible"].Value;

    private string NombreRequisito(RequisitoPlantillaDto r) =>
        _refs.Tipos.TryGetValue(r.TipoDocumentoId, out var tipo) ? tipo.Nombre : Textos["RequisitoNoDisponible"].Value;

    private static string ClaveExplicacion(TipoPlantillaRequisitos tipo) => tipo switch
    {
        TipoPlantillaRequisitos.PorActividad => "ExplicaActividad",
        TipoPlantillaRequisitos.PorRiesgo => "ExplicaRiesgo",
        TipoPlantillaRequisitos.DeCentro => "ExplicaCentro",
        _ => "ExplicaExigencia",
    };

    private Task Buscar(string texto)
    {
        _busqueda = texto ?? string.Empty;
        Recalcular();
        return CargarDetalleAsync();
    }

    private Task CambiarNiveles(string? valor)
    {
        _niveles = valor;
        Recalcular();
        return CargarDetalleAsync();
    }

    private async Task ElegirAsync(Guid id)
    {
        _seleccion = id;
        await CargarDetalleAsync();
    }

    private async Task CargarDetalleAsync()
    {
        var elegida = SeleccionVisible;
        if (elegida is null)
        {
            _detalle = null;
            return;
        }

        if (_detalle?.Id == elegida.Plantilla.Id && !_errorDetalle)
            return;

        _cargandoDetalle = true;
        _errorDetalle = false;
        _detalle = null;
        try
        {
            _detalle = await Mediator.Send(new ObtenerPlantillaRequisitosPorIdQuery(elegida.Plantilla.Id));
            _errorDetalle = _detalle is null;
        }
        catch (Exception)
        {
            _errorDetalle = true;
        }
        finally
        {
            _cargandoDetalle = false;
        }
    }

    private void CerrarAlta(bool visible)
    {
        if (!visible)
            _altaVisible = false;
    }

    private async Task AltaCreadaAsync(Guid id)
    {
        _altaVisible = false;
        _seleccion = id;
        _detalle = null;
        ToastService.Mostrar(Textos["ToastAlta"], TonoToast.Exito);
        await CargarAsync();
    }

    private void CerrarRetirar(bool visible)
    {
        if (visible)
            return;
        _retirando = false;
        _errorRetirar = null;
    }

    private async Task RetirarAsync()
    {
        if (_detalle is null)
        {
            _errorRetirar = Textos["ErrorDetalleDetalle"];
            return;
        }

        _enProgreso = true;
        _errorRetirar = null;
        try
        {
            var resultado = await Mediator.Send(new RetirarPlantillaRequisitosCommand(_detalle.Id, _detalle.Version));
            if (!resultado.EsExitoso)
            {
                _errorRetirar = resultado.Error.Mensaje;
                // Un conflicto de versión se resuelve volviendo a leer la plantilla.
                _detalle = null;
                await CargarDetalleAsync();
                return;
            }

            _retirando = false;
            _seleccion = null;
            _detalle = null;
            ToastService.Mostrar(Textos["ToastRetirada"], TonoToast.Exito);
            await CargarAsync();
        }
        catch (Exception)
        {
            _errorRetirar = Textos["ErrorGuardar"];
        }
        finally
        {
            _enProgreso = false;
        }
    }
}
