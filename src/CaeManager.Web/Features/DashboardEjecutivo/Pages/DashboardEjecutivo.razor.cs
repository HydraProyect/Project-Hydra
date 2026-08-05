using ApexCharts;
using CaeManager.Application.Dashboard.Catalogo;
using CaeManager.Application.Dashboard.Commands;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Web.Components.DesignSystem;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.DashboardEjecutivo.Pages;

public record SegmentoGraficoDto(string Etiqueta, decimal Valor);

public partial class DashboardEjecutivo : ComponentBase
{
    private static readonly ApexChartOptions<SegmentoGraficoDto> OpcionesBarraHorizontal = new()
    {
        PlotOptions = new PlotOptions { Bar = new PlotOptionsBar { Horizontal = true } }
    };

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private ILogger<DashboardEjecutivo> Logger { get; set; } = default!;

    private DashboardEjecutivoDto? _valores;
    private IReadOnlyList<string>? _seleccionGuardada;
    private HashSet<string> _seleccionEnEdicion = [];
    private bool _error;
    private bool _guardando;

    private List<SegmentoGraficoDto> _semaforoDocumental = [];
    private List<SegmentoGraficoDto> _incidenciasPorGravedad = [];
    private List<SegmentoGraficoDto> _centrosConMasRiesgo = [];

    protected override Task OnInitializedAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        _error = false;
        _valores = null;
        _seleccionGuardada = null;
        StateHasChanged();

        try
        {
            var (seleccion, valores) = (
                await Mediator.Send(new ObtenerPreferenciaDashboardQuery()),
                await Mediator.Send(new ObtenerDashboardEjecutivoQuery()));

            _seleccionGuardada = seleccion;
            _seleccionEnEdicion = [.. seleccion];
            _valores = valores;
            RecalcularSeriesDeGraficos();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al cargar el Dashboard Ejecutivo.");
            _error = true;
        }
    }

    private void RecalcularSeriesDeGraficos()
    {
        if (_valores is null) return;

        _semaforoDocumental =
        [
            new("Vigente", _valores.DocumentosVigentes),
            new("Próximo", _valores.DocumentosProximos),
            new("Urgente", _valores.DocumentosUrgentes),
            new("Vencido", _valores.DocumentosVencidos),
        ];

        _incidenciasPorGravedad = _valores.IncidenciasPorGravedad
            .Select(g => new SegmentoGraficoDto(g.Gravedad.ToString(), g.Cantidad))
            .ToList();

        _centrosConMasRiesgo = _valores.CentrosConMasRiesgo
            .Select(c => new SegmentoGraficoDto($"{c.TenantNombre} · {c.CentroNombre}", (decimal)c.PuntuacionMedia))
            .ToList();
    }

    private void AlternarSeleccion(string codigo, bool seleccionado)
    {
        if (seleccionado) _seleccionEnEdicion.Add(codigo);
        else _seleccionEnEdicion.Remove(codigo);
    }

    private async Task GuardarSeleccionAsync()
    {
        _guardando = true;
        try
        {
            var resultado = await Mediator.Send(new GuardarPreferenciaDashboardCommand([.. _seleccionEnEdicion]));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            _seleccionGuardada = [.. _seleccionEnEdicion];
            ToastService.Mostrar("Selección guardada.", TonoToast.Exito);
        }
        finally
        {
            _guardando = false;
        }
    }

    private static TonoBadge TonoPorcentaje(int valor) => valor switch
    {
        >= 90 => TonoBadge.Exito,
        >= 70 => TonoBadge.Advertencia,
        _ => TonoBadge.Peligro
    };

    private string ValorTile(string codigo) => codigo switch
    {
        CatalogoKpis.TrabajadoresActivos => _valores!.TrabajadoresActivos.ToString(),
        CatalogoKpis.Centros => _valores!.Centros.ToString(),
        CatalogoKpis.VisitasProgramadas => _valores!.VisitasProgramadas.ToString(),
        CatalogoKpis.VisitasUrgentes => _valores!.VisitasUrgentes.ToString(),
        CatalogoKpis.TasaCumplimiento => $"{_valores!.TasaCumplimiento}%",
        CatalogoKpis.PuntuacionMediaEvaluaciones => _valores!.PuntuacionMediaEvaluaciones is { } p ? $"{p:F0}" : "—",
        CatalogoKpis.IncidenciasAbiertas => _valores!.IncidenciasAbiertas.ToString(),
        CatalogoKpis.TiempoMedioResolucionIncidencias => _valores!.TiempoMedioResolucionIncidenciasDias is { } d ? $"{d:F1} días" : "—",
        CatalogoKpis.ConfianzaMediaIa => _valores!.ConfianzaMediaIa is { } c ? $"{c:F0}%" : "—",
        CatalogoKpis.CosteMesActualIa => $"{_valores!.CosteIaMesActual:F2} €",
        CatalogoKpis.TiempoMedioProcesamientoIa => _valores!.TiempoMedioProcesamientoIaMs is { } t ? $"{t:F0} ms" : "—",
        CatalogoKpis.FacturacionEstimadaMesActual => $"{_valores!.FacturacionEstimadaMesActual:F2} €",
        _ => "—"
    };

    private TonoBadge TonoTile(string codigo) => codigo switch
    {
        CatalogoKpis.TasaCumplimiento => TonoPorcentaje(_valores!.TasaCumplimiento),
        CatalogoKpis.ConfianzaMediaIa when _valores!.ConfianzaMediaIa is { } c => TonoPorcentaje((int)c),
        _ => TonoBadge.Neutro
    };
}
