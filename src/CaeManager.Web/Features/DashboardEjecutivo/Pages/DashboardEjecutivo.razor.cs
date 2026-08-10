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
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    private DashboardEjecutivoDto? _valores;
    private IReadOnlyList<string>? _seleccionGuardada;
    private HashSet<string> _seleccionEnEdicion = [];
    private bool _error;
    private bool _guardando;

    private List<SegmentoGraficoDto> _semaforoDocumental = [];
    private List<SegmentoGraficoDto> _incidenciasPorGravedad = [];
    private List<SegmentoGraficoDto> _centrosConMenorCumplimiento = [];
    private EstadisticasAprobacionDocumentoDto? _estadisticasAprobacion;
    private IReadOnlyList<RiesgoEmpresaDto> _empresasEnRiesgo = [];

    protected override Task OnInitializedAsync() => CargarAsync();

    private void IrAEmpresa(string empresaRazonSocial) => NavigationManager.NavigateTo($"/empresas?q={Uri.EscapeDataString(empresaRazonSocial)}");

    private async Task CargarAsync()
    {
        _error = false;
        _valores = null;
        _seleccionGuardada = null;
        StateHasChanged();

        try
        {
            var (seleccion, valores, estadisticasAprobacion, desglose) = (
                await Mediator.Send(new ObtenerPreferenciaDashboardQuery()),
                await Mediator.Send(new ObtenerDashboardEjecutivoQuery()),
                await Mediator.Send(new ObtenerEstadisticasAprobacionDocumentoQuery()),
                await Mediator.Send(new ObtenerDesgloseDashboardQuery()));

            _seleccionGuardada = seleccion;
            _seleccionEnEdicion = [.. seleccion];
            _valores = valores;
            _estadisticasAprobacion = estadisticasAprobacion;
            _empresasEnRiesgo = desglose.EmpresasEnRiesgo;
            RecalcularSeriesDeGraficos();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al cargar el Dashboard Ejecutivo.");
            _error = true;
        }
    }

    /// <summary>Total de decisiones de verificación IA ya resueltas (automáticas + manuales) — 0 si todavía no se ha verificado ningún Documento.</summary>
    private int TotalAprobaciones => (_estadisticasAprobacion?.Automaticas ?? 0) + (_estadisticasAprobacion?.Manuales ?? 0);

    private int PorcentajeAutomatica => TotalAprobaciones == 0 ? 0 : _estadisticasAprobacion!.Automaticas * 100 / TotalAprobaciones;

    private int PorcentajeManual => TotalAprobaciones == 0 ? 0 : 100 - PorcentajeAutomatica;

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

        _centrosConMenorCumplimiento = _valores.CentrosConMenorCumplimiento
            .Select(c => new SegmentoGraficoDto($"{c.TenantNombre} · {c.CentroNombre}", c.Porcentaje))
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
        CatalogoKpis.PorcentajeCumplimientoDocumental => _valores!.PorcentajeCumplimientoDocumental is { } p ? $"{p:F0}%" : "—",
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
        CatalogoKpis.PorcentajeCumplimientoDocumental when _valores!.PorcentajeCumplimientoDocumental is { } p => TonoPorcentaje((int)p),
        CatalogoKpis.ConfianzaMediaIa when _valores!.ConfianzaMediaIa is { } c => TonoPorcentaje((int)c),
        _ => TonoBadge.Neutro
    };
}
