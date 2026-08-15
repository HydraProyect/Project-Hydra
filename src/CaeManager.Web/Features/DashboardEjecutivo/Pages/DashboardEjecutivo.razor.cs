using CaeManager.Web.Features.Visitas;
using ApexCharts;
using CaeManager.Application.Dashboard;
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

    /// <summary>
    /// Periodo elegido. Arranca en el mes en curso, que es exactamente lo que hacía el
    /// panel antes de que el periodo fuera elegible — cambiar el valor por defecto habría
    /// movido los números de todo el mundo sin avisar.
    /// </summary>
    private PresetPeriodoKpi _preset = PresetPeriodoKpi.MesActual;
    private string _desdePersonalizado = string.Empty;
    private string _hastaPersonalizado = string.Empty;
    private IReadOnlyList<string>? _seleccionGuardada;
    private HashSet<string> _seleccionEnEdicion = [];
    private bool _error;
    private bool _guardando;

    private List<SegmentoGraficoDto> _semaforoDocumental = [];
    private List<SegmentoGraficoDto> _incidenciasPorGravedad = [];
    private List<SegmentoGraficoDto> _centrosConMenorCumplimiento = [];
    private List<SegmentoGraficoDto> _distribucionAntelacion = [];
    private List<SegmentoGraficoDto> _atribucionUrgencia = [];
    private List<SegmentoGraficoDto> _ocupacionGestores = [];
    private EstadisticasAprobacionDocumentoDto? _estadisticasAprobacion;
    private IReadOnlyList<RiesgoEmpresaDto> _empresasEnRiesgo = [];

    protected override Task OnInitializedAsync()
    {
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        _desdePersonalizado = new DateOnly(hoy.Year, hoy.Month, 1).ToString("yyyy-MM-dd");
        _hastaPersonalizado = hoy.ToString("yyyy-MM-dd");
        return CargarAsync();
    }

    /// <summary>Presets del selector — los rangos que alguien pide de verdad, más uno libre.</summary>
    private enum PresetPeriodoKpi { MesActual, MesAnterior, UltimosTresMeses, AnyoActual, Personalizado }

    private static string EtiquetaPreset(PresetPeriodoKpi preset) => preset switch
    {
        PresetPeriodoKpi.MesAnterior => "Mes anterior",
        PresetPeriodoKpi.UltimosTresMeses => "Últimos 3 meses",
        PresetPeriodoKpi.AnyoActual => "Año en curso",
        PresetPeriodoKpi.Personalizado => "Personalizado",
        _ => "Mes en curso"
    };

    private PeriodoKpi PeriodoSeleccionado()
    {
        var ahora = DateTime.UtcNow;
        var inicioMes = new DateTime(ahora.Year, ahora.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        return _preset switch
        {
            PresetPeriodoKpi.MesAnterior => new PeriodoKpi(inicioMes.AddMonths(-1), inicioMes),
            PresetPeriodoKpi.UltimosTresMeses => new PeriodoKpi(inicioMes.AddMonths(-2), inicioMes.AddMonths(1)),
            PresetPeriodoKpi.AnyoActual => new PeriodoKpi(new DateTime(ahora.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc), inicioMes.AddMonths(1)),
            PresetPeriodoKpi.Personalizado when DateOnly.TryParse(_desdePersonalizado, out var desde) && DateOnly.TryParse(_hastaPersonalizado, out var hasta) && hasta >= desde
                => PeriodoKpi.DeFechas(desde, hasta),
            _ => PeriodoKpi.MesActual(ahora)
        };
    }

    private async Task CambiarPresetAsync(string valor)
    {
        if (!Enum.TryParse<PresetPeriodoKpi>(valor, out var preset)) return;

        _preset = preset;
        await CargarAsync();
    }

    /// <summary>Solo recarga si el rango personalizado está completo y es válido — no tiene sentido consultar mientras el usuario está a medio escribir una fecha.</summary>
    private async Task AplicarRangoPersonalizadoAsync()
    {
        if (_preset != PresetPeriodoKpi.Personalizado) return;
        if (!DateOnly.TryParse(_desdePersonalizado, out var desde) || !DateOnly.TryParse(_hastaPersonalizado, out var hasta)) return;
        if (hasta < desde) return;

        await CargarAsync();
    }

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
                await Mediator.Send(new ObtenerDashboardEjecutivoQuery(PeriodoSeleccionado())),
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

        _distribucionAntelacion = _valores.Bpo.DistribucionAntelacion
            .Select(t => new SegmentoGraficoDto(AntelacionVisitaUi.Texto(t.Tramo), t.Cantidad))
            .ToList();

        _atribucionUrgencia = _valores.Bpo.AtribucionUrgencia
            .Select(a => new SegmentoGraficoDto(AntelacionVisitaUi.TextoAtribucion(a.Atribucion), a.Cantidad))
            .ToList();

        _ocupacionGestores = _valores.Bpo.OcupacionPorGestor
            .Select(o => new SegmentoGraficoDto(o.Nombre, o.PorcentajeOcupacion ?? 0))
            .ToList();
    }

    /// <summary>Palanca IA: confirmadas de un clic sobre todas las resueltas. Sin resoluciones no hay porcentaje que enseñar, y un 0% mentiría.</summary>
    private int? PorcentajePalancaIa => _valores!.Bpo.TotalSugerenciasResueltas == 0
        ? null
        : (int)Math.Round(_valores.Bpo.SugerenciasConfirmadasSinEdicion * 100.0 / _valores.Bpo.TotalSugerenciasResueltas);

    private int? PorcentajeFalsosAvisos => _valores!.Bpo.TotalVisitasConAntelacionMedida == 0
        ? null
        : (int)Math.Round(_valores.Bpo.VisitasConFalsoAviso * 100.0 / _valores.Bpo.TotalVisitasConAntelacionMedida);

    private decimal? HorasBloqueadasMedia => _valores!.Bpo.TotalVisitasConAntelacionMedida == 0
        ? null
        : _valores.Bpo.HorasBloqueadasPorClienteTotal / _valores.Bpo.TotalVisitasConAntelacionMedida;

    private static string Horas(int segundos) => $"{segundos / 3600.0:F1} h";

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
        CatalogoKpis.PalancaIa => PorcentajePalancaIa is { } pi ? $"{pi}%" : "—",
        CatalogoKpis.FalsosAvisos => PorcentajeFalsosAvisos is { } fa ? $"{fa}%" : "—",
        CatalogoKpis.TiempoBloqueadoCliente => HorasBloqueadasMedia is { } hb ? $"{hb:F1} h" : "—",
        _ => "—"
    };

    private TonoBadge TonoTile(string codigo) => codigo switch
    {
        CatalogoKpis.TasaCumplimiento => TonoPorcentaje(_valores!.TasaCumplimiento),
        CatalogoKpis.PorcentajeCumplimientoDocumental when _valores!.PorcentajeCumplimientoDocumental is { } p => TonoPorcentaje((int)p),
        CatalogoKpis.ConfianzaMediaIa when _valores!.ConfianzaMediaIa is { } c => TonoPorcentaje((int)c),
        CatalogoKpis.PalancaIa when PorcentajePalancaIa is { } pi => TonoPorcentaje(pi),
        // Invertido a propósito: aquí un porcentaje alto es un problema del cliente,
        // no un logro. 30% de falsos avisos no puede pintarse en verde.
        CatalogoKpis.FalsosAvisos when PorcentajeFalsosAvisos is { } fa => TonoPorcentaje(100 - fa),
        // Aviso al operador (Horizonte 2.7): límite blando de gasto en IA por
        // tenant, superado — ver ParametroSistema.PresupuestoMensualIaUsd.
        // No bloquea nada, solo cambia el semáforo de la tile a rojo.
        CatalogoKpis.CosteMesActualIa when TenantsConPresupuestoIaExcedido.Count > 0 => TonoBadge.Peligro,
        _ => TonoBadge.Neutro
    };

    /// <summary>Tenants (de entre los que este operador ve en el fan-out) cuyo gasto de IA del período supera su presupuesto mensual configurado — vacío si ninguno tiene presupuesto configurado o si todos están dentro.</summary>
    private IReadOnlyList<string> TenantsConPresupuestoIaExcedido => _valores?.TenantsConPresupuestoIaExcedido ?? [];
}
