using System.Globalization;
using CaeManager.Application.Dashboard;
using CaeManager.Application.Dashboard.Catalogo;
using CaeManager.Application.Dashboard.Commands;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Domain.Incidencias;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Visitas;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.DashboardEjecutivo.Pages;

/// <summary>
/// Dashboard Ejecutivo contra su mockup Gen 2 («Dashboard Ejecutivo TALVEG.dc.html»).
///
/// <para>
/// <b>Dos alcances distintos en la misma pantalla, y se dicen.</b>
/// <see cref="ObtenerDashboardEjecutivoQuery"/> hace fan-out por organización
/// autorizada (tenant de origen más los Delegated Workspaces activos) y fusiona
/// en memoria. Las otras dos consultas —
/// <see cref="ObtenerEstadisticasAprobacionDocumentoQuery"/> y
/// <see cref="ObtenerDesgloseDashboardQuery"/>— se piden SIN
/// <c>AmbitoTenantExplicito</c>, así que responden con el filtro global del
/// tenant activo: son de UNA organización. Mientras la consulta no recorra las
/// demás, lo que sale de ellas se rotula como tal
/// (<see cref="AvisoSoloOrganizacionActiva"/>) en vez de presentarse junto a
/// sumas de varias como si midiera lo mismo.
/// </para>
///
/// <para>
/// <b>Cargas:</b> la vigente es la última (<see cref="_versionCarga"/>) y la
/// retirada cancela la que siga en vuelo (<see cref="_ciclo"/>); lo que vuelve
/// de una carga que ya no es la vigente no toca el estado, ni datos ni error.
/// </para>
/// </summary>
public partial class DashboardEjecutivo : ComponentBase, IDisposable
{
    /// <summary>
    /// El producto es monolingüe: el nombre del mes del rótulo de periodo no
    /// puede depender de la configuración regional del servidor. Program.cs ya
    /// fija es-ES por hilo, pero un rótulo que dijera «September» ante un
    /// default distinto sería un defecto silencioso.
    /// </summary>
    private static readonly CultureInfo Castellano = CultureInfo.GetCultureInfo("es-ES");

    // Geometría del donut, en unidades del viewBox y en ENTEROS: un decimal
    // interpolado saldría con la coma de es-ES y el navegador descartaría el
    // atributo (mismo motivo que AnilloCumplimiento.RadioSvg).
    private const int LadoDonut = 180;
    private const int CentroDonut = 90;
    private const int RadioDonut = 62;
    private const int GrosorDonut = 26;

    /// <summary>2π·62 = 389,56, redondeado a entero por lo dicho arriba; los arcos reparten exactamente esta longitud.</summary>
    private const int CircunferenciaDonut = 390;

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private ILogger<DashboardEjecutivo> Logger { get; set; } = default!;

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

    private EstadisticasAprobacionDocumentoDto? _estadisticasAprobacion;
    private IReadOnlyList<RiesgoEmpresaDto> _empresasEnRiesgo = [];

    /// <summary>
    /// Número de la carga vigente. Cada <see cref="CargarAsync"/> se queda con
    /// uno nuevo; lo que vuelve de una carga que ya no es la vigente no toca el
    /// estado.
    /// </summary>
    private int _versionCarga;

    private readonly CancellationTokenSource _ciclo = new();
    private bool _desechado;

    protected override Task OnInitializedAsync()
    {
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        _desdePersonalizado = new DateOnly(hoy.Year, hoy.Month, 1).ToString("yyyy-MM-dd");
        _hastaPersonalizado = hoy.ToString("yyyy-MM-dd");
        return CargarAsync();
    }

    /// <summary>
    /// Blazor llama a <c>Dispose</c> y no a <c>DisposeAsync</c> en un componente
    /// que solo implementa <see cref="IDisposable"/>: implementar los dos dejaría
    /// la mitad del cierre sin ejecutar.
    /// </summary>
    public void Dispose()
    {
        if (_desechado) return;

        _desechado = true;
        _ciclo.Cancel();
        _ciclo.Dispose();
    }

    private bool EsVigente(int version) => !_desechado && version == _versionCarga;

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

    private async Task CargarAsync()
    {
        if (_desechado) return;

        var version = ++_versionCarga;
        var token = _ciclo.Token;
        _error = false;
        _valores = null;
        _seleccionGuardada = null;

        // La carga establece un contexto nuevo para el panel: la bandera de
        // guardado en curso pertenecía al anterior, y el guardado que siga en
        // vuelo ya no la apagará (solo la apaga quien sigue siendo vigente).
        _guardando = false;
        StateHasChanged();

        IReadOnlyList<string> seleccion;
        DashboardEjecutivoDto valores;
        EstadisticasAprobacionDocumentoDto estadisticasAprobacion;
        DesgloseDashboardDto desglose;
        try
        {
            seleccion = await Mediator.Send(new ObtenerPreferenciaDashboardQuery(), token);
            valores = await Mediator.Send(new ObtenerDashboardEjecutivoQuery(PeriodoSeleccionado()), token);
            estadisticasAprobacion = await Mediator.Send(new ObtenerEstadisticasAprobacionDocumentoQuery(), token);
            desglose = await Mediator.Send(new ObtenerDesgloseDashboardQuery(), token);
        }
        catch (Exception ex)
        {
            if (!EsVigente(version)) return;
            Logger.LogError(ex, "Error al cargar el Dashboard Ejecutivo.");
            _error = true;
            return;
        }

        if (!EsVigente(version)) return;

        _seleccionGuardada = seleccion;
        _seleccionEnEdicion = [.. seleccion];
        _valores = valores;
        _estadisticasAprobacion = estadisticasAprobacion;
        _empresasEnRiesgo = desglose.EmpresasEnRiesgo;
    }

    // ---------------------------------------------------------------- selección

    /// <summary>
    /// Los KPI que se pintan: los guardados que siguen existiendo en el catálogo,
    /// en el orden en que los devolvió la consulta. Un código que ya no existe
    /// (el catálogo cambió) se descarta aquí igual que lo descarta
    /// <see cref="ObtenerPreferenciaDashboardQueryHandler"/>.
    /// </summary>
    private IReadOnlyList<DefinicionKpi> Seleccionados => _seleccionGuardada is null
        ? []
        : _seleccionGuardada
            .Select(codigo => CatalogoKpis.Todos.FirstOrDefault(k => k.Codigo == codigo))
            .Where(k => k is not null)
            .Select(k => k!)
            .ToList();

    private IReadOnlyList<DefinicionKpi> Tiles => Seleccionados
        .Where(k => k.TipoRender is TipoRenderKpi.TileNumerico or TipoRenderKpi.TilePorcentajeConTono)
        .ToList();

    private IReadOnlyList<DefinicionKpi> Graficos => Seleccionados
        .Where(k => k.TipoRender is TipoRenderKpi.GraficoDonut or TipoRenderKpi.GraficoBarras
            or TipoRenderKpi.TablaRiesgo or TipoRenderKpi.BarraComparativa)
        .ToList();

    /// <summary>
    /// Cuenta los que se PINTAN sobre el catálogo entero, no la longitud de la
    /// lista guardada: un código huérfano no se muestra, así que tampoco cuenta
    /// como mostrado.
    /// </summary>
    private string ResumenSeleccion =>
        $"{Seleccionados.Count} de {CatalogoKpis.Todos.Count} KPI mostrados";

    /// <summary>
    /// Marcar no aplica nada: hasta pulsar Guardar, lo marcado y lo guardado son
    /// listas distintas y el panel se puede cerrar perdiendo lo marcado. El
    /// mockup Gen 2 pide decirlo.
    /// </summary>
    private bool HayCambiosSinGuardar => _seleccionGuardada is not null
        && !_seleccionEnEdicion.SetEquals(_seleccionGuardada);

    private void AlternarSeleccion(string codigo, bool seleccionado)
    {
        if (seleccionado) _seleccionEnEdicion.Add(codigo);
        else _seleccionEnEdicion.Remove(codigo);
    }

    /// <summary>
    /// Guarda lo que estaba marcado AL PULSAR, y solo declara guardado eso: leer
    /// <c>_seleccionEnEdicion</c> después del await daría por guardado lo que el
    /// usuario marcase mientras el comando viajaba, que nunca se envió.
    /// </summary>
    private async Task GuardarSeleccionAsync()
    {
        // Guarda de reentrada al entrar: el botón deshabilitado no basta, porque
        // el clic ya viajaba cuando se deshabilitó.
        if (_guardando || _desechado) return;

        var version = _versionCarga;
        var token = _ciclo.Token;
        var enviada = _seleccionEnEdicion.ToList();
        _guardando = true;

        try
        {
            var resultado = await Mediator.Send(new GuardarPreferenciaDashboardCommand(enviada), token);

            if (!EsVigente(version)) return;

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            _seleccionGuardada = enviada;
            ToastService.Mostrar("Selección guardada.", TonoToast.Exito);
        }
        catch (Exception ex)
        {
            if (!EsVigente(version)) return;
            Logger.LogError(ex, "Error al guardar la selección de KPIs del Dashboard Ejecutivo.");
            ToastService.Mostrar("No pudimos guardar la selección. Intenta nuevamente.", TonoToast.Error);
        }
        finally
        {
            // Solo la apaga quien sigue siendo vigente: si entre medias hubo una
            // carga nueva, el final de este guardado no reabre el panel de otra.
            if (EsVigente(version)) _guardando = false;
        }
    }

    // ---------------------------------------------------------------- alcance y periodo

    private int Organizaciones => _valores?.TotalTenants ?? 0;

    private string TextoOrganizaciones => Organizaciones == 1 ? "1 organización" : $"{Organizaciones} organizaciones";

    /// <summary>Rango consultado, con el día de fin inclusivo: <see cref="PeriodoKpi"/> es semiabierto y su FinUtc es exclusivo.</summary>
    private string PeriodoTexto
    {
        get
        {
            var periodo = PeriodoSeleccionado();
            var desde = DateOnly.FromDateTime(periodo.InicioUtc);
            var hasta = DateOnly.FromDateTime(periodo.FinUtc.AddDays(-1));
            var rango = desde == hasta ? Fecha(desde) : $"Del {Fecha(desde)} al {Fecha(hasta)}";
            return $"{rango} · {TextoOrganizaciones}";
        }
    }

    private static string Fecha(DateOnly fecha) => fecha.ToString("d 'de' MMMM 'de' yyyy", Castellano);

    private string DetalleOrganizaciones =>
        $"{TextoOrganizaciones} en el alcance: la tuya más las que os han delegado su gestión CAE. Los KPI de esta sección suman las {Organizaciones}.";

    /// <summary>
    /// Rótulo para lo que sale de una consulta que NO recorre las demás
    /// organizaciones. Nulo con una sola, donde no hay nada que distinguir.
    /// </summary>
    private string? AvisoSoloOrganizacionActiva => Organizaciones > 1
        ? "Solo la organización activa: esta consulta no recorre las demás."
        : null;

    // ---------------------------------------------------------------- tiles

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
        // Invertido a propósito: aquí un porcentaje alto es un problema del
        // Cliente empresarial, no un logro. 30% de falsos avisos no puede
        // pintarse en verde — y la Pista lo dice, porque el color no puede ser
        // el único portador del significado (02 § 8).
        CatalogoKpis.FalsosAvisos when PorcentajeFalsosAvisos is { } fa => TonoPorcentaje(100 - fa),
        // Aviso al operador (Horizonte 2.7): límite blando de gasto en IA por
        // tenant, superado — ver ParametroSistema.PresupuestoMensualIaUsd.
        // No bloquea nada, solo cambia el semáforo de la tile a rojo.
        CatalogoKpis.CosteMesActualIa when TenantsConPresupuestoIaExcedido.Count > 0 => TonoBadge.Peligro,
        _ => TonoBadge.Neutro
    };

    /// <summary>
    /// Tercera línea de la tile. Solo se rellena con un dato real o con una
    /// advertencia de método que el código cumple de verdad —la ponderación del
    /// merge, el tono invertido—: el mockup pinta ahí comparativas contra el
    /// periodo anterior y denominadores («12 altas este mes», «sobre 4.212
    /// obligatorios») que ninguna consulta devuelve hoy.
    /// </summary>
    private string? PistaTile(string codigo) => codigo switch
    {
        CatalogoKpis.TasaCumplimiento => "Ponderada por el volumen de documentos de cada organización",
        CatalogoKpis.PorcentajeCumplimientoDocumental => "Ponderada por lo que se pide en cada organización",
        CatalogoKpis.ConfianzaMediaIa => "Ponderada por las extracciones de cada organización",
        CatalogoKpis.TiempoMedioResolucionIncidencias => "Ponderado por las incidencias resueltas de cada organización",
        CatalogoKpis.FacturacionEstimadaMesActual => "Solo los Clientes empresariales con tarifas configuradas",
        CatalogoKpis.FalsosAvisos => "Tono invertido: un porcentaje alto es el problema",
        // Vacío no distingue «todas dentro de su presupuesto» de «ninguna tiene
        // presupuesto configurado», así que sin excedidos no se afirma nada.
        CatalogoKpis.CosteMesActualIa when TenantsConPresupuestoIaExcedido.Count > 0 => TenantsConPresupuestoIaExcedido.Count == 1
            ? "1 organización sobre su presupuesto de IA"
            : $"{TenantsConPresupuestoIaExcedido.Count} organizaciones sobre su presupuesto de IA",
        _ => null
    };

    private static TonoBadge TonoPorcentaje(int valor) => valor switch
    {
        >= 90 => TonoBadge.Exito,
        >= 70 => TonoBadge.Advertencia,
        _ => TonoBadge.Peligro
    };

    /// <summary>Tenants (de entre los que este operador ve en el fan-out) cuyo gasto de IA del período supera su presupuesto mensual configurado — vacío si ninguno tiene presupuesto configurado o si todos están dentro.</summary>
    private IReadOnlyList<string> TenantsConPresupuestoIaExcedido => _valores?.TenantsConPresupuestoIaExcedido ?? [];

    /// <summary>Total de decisiones de verificación IA ya resueltas (automáticas + manuales) — 0 si todavía no se ha verificado ningún Documento.</summary>
    private int TotalAprobaciones => (_estadisticasAprobacion?.Automaticas ?? 0) + (_estadisticasAprobacion?.Manuales ?? 0);

    /// <summary>
    /// Redondeado, no truncado: 312 de 459 es el 67,97%, y la división entera
    /// lo dejaba en 67 — un punto perdido que también acortaba el segmento de
    /// la barra. El manual es el complemento, así que los dos siempre suman 100.
    /// </summary>
    private int PorcentajeAutomatica => TotalAprobaciones == 0
        ? 0
        : (int)Math.Round(_estadisticasAprobacion!.Automaticas * 100.0 / TotalAprobaciones);

    private int PorcentajeManual => TotalAprobaciones == 0 ? 0 : 100 - PorcentajeAutomatica;

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

    // ---------------------------------------------------------------- donut

    private sealed record SegmentoDonut(string Etiqueta, int Valor, string Clase);

    /// <param name="Longitud">Trozo de la circunferencia que ocupa este arco.</param>
    /// <param name="Resto">Lo que queda hasta cerrar el círculo — la otra mitad del stroke-dasharray.</param>
    /// <param name="Desplazamiento">Dónde empieza el arco, ya negado para el stroke-dashoffset.</param>
    private sealed record ArcoDonut(string Clase, int Longitud, int Resto, int Desplazamiento);

    private sealed record LineaLeyenda(string Etiqueta, string Clase, string Valor, string Desglose);

    private IReadOnlyList<SegmentoDonut> SegmentosDonut(string codigo) => codigo switch
    {
        CatalogoKpis.SemaforoDocumental =>
        [
            new("Vigente", _valores!.DocumentosVigentes, "segmento-exito"),
            new("Próximo", _valores.DocumentosProximos, "segmento-advertencia"),
            new("Urgente", _valores.DocumentosUrgentes, "segmento-advertencia-fuerte"),
            new("Vencido", _valores.DocumentosVencidos, "segmento-peligro"),
        ],
        CatalogoKpis.DistribucionAntelacion => _valores!.Bpo.DistribucionAntelacion
            .Select(t => new SegmentoDonut(
                AntelacionVisitaUi.Texto(t.Tramo), t.Cantidad, ClaseTono(AntelacionVisitaUi.Tono(t.Tramo))))
            .ToList(),
        _ => []
    };

    /// <summary>Qué se está contando, para que el total del donut no sea un número desnudo.</summary>
    private static string UnidadDonut(string codigo) => codigo switch
    {
        CatalogoKpis.SemaforoDocumental => "documentos con vigencia",
        CatalogoKpis.DistribucionAntelacion => "visitas con la antelación medida",
        _ => "registros"
    };

    /// <summary>
    /// Reparte la circunferencia entera entre los segmentos: el último se queda
    /// con lo que sobra del redondeo, para que el anillo cierre exactamente y no
    /// quede una rendija que insinúe una categoría que no existe.
    /// </summary>
    private static IReadOnlyList<ArcoDonut> ArcosDonut(IReadOnlyList<SegmentoDonut> segmentos)
    {
        var conValor = segmentos.Where(s => s.Valor > 0).ToList();
        var total = conValor.Sum(s => s.Valor);
        if (total == 0) return [];

        var arcos = new List<ArcoDonut>(conValor.Count);
        var acumulado = 0;
        for (var i = 0; i < conValor.Count; i++)
        {
            var longitud = i == conValor.Count - 1
                ? CircunferenciaDonut - acumulado
                : (int)Math.Round(CircunferenciaDonut * (double)conValor[i].Valor / total);
            arcos.Add(new ArcoDonut(conValor[i].Clase, longitud, CircunferenciaDonut - longitud, -acumulado));
            acumulado += longitud;
        }

        return arcos;
    }

    private static IReadOnlyList<LineaLeyenda> LeyendaDonut(IReadOnlyList<SegmentoDonut> segmentos, string unidad)
    {
        var total = segmentos.Sum(s => s.Valor);
        return segmentos.Select(s => new LineaLeyenda(
            s.Etiqueta,
            s.Clase,
            total == 0 ? s.Valor.ToString() : $"{s.Valor} ({Math.Round(s.Valor * 100.0 / total)}%)",
            total == 0
                ? $"{s.Etiqueta}: {s.Valor} de 0 {unidad}."
                : $"{s.Etiqueta}: {s.Valor} de {total} {unidad}, el {Math.Round(s.Valor * 100.0 / total)}% del total."))
            .ToList();
    }

    // ---------------------------------------------------------------- barras

    /// <param name="Ancho">Porcentaje del carril que ocupa la barra; 0 cuando no hay dato que dibujar.</param>
    /// <param name="SinDato">Por qué no hay barra, cuando no la hay. Un 0% dibujado sería indistinguible de una ausencia.</param>
    private sealed record BarraKpi(string Etiqueta, string Valor, int Ancho, string Clase, string? SinDato = null);

    private IReadOnlyList<BarraKpi> BarrasKpi(string codigo) => codigo switch
    {
        CatalogoKpis.IncidenciasPorGravedad => Proporcionales(
            _valores!.IncidenciasPorGravedad
                .OrderBy(g => g.Gravedad)
                .Select(g => (Etiqueta: EtiquetaGravedad(g.Gravedad), Valor: g.Cantidad, Clase: ClaseTono(TonoGravedad(g.Gravedad))))
                .ToList()),
        CatalogoKpis.AtribucionUrgencia => Proporcionales(
            _valores!.Bpo.AtribucionUrgencia
                .OrderByDescending(a => a.Cantidad)
                .Select(a => (Etiqueta: AntelacionVisitaUi.TextoAtribucion(a.Atribucion), Valor: a.Cantidad, Clase: "segmento-neutro"))
                .ToList()),
        // Ya son porcentajes: el carril es el 100%, no el mayor de la serie.
        CatalogoKpis.CentrosConMenorCumplimiento => _valores!.CentrosConMenorCumplimiento
            .Select(c => new BarraKpi(
                $"{c.TenantNombre} · {c.CentroNombre}",
                $"{Math.Clamp(c.Porcentaje, 0, 100)}%",
                Math.Clamp(c.Porcentaje, 0, 100),
                ClaseTono(TonoPorcentaje(c.Porcentaje))))
            .ToList(),
        // Ocupación sin jornada mensual configurada: el DTO trae PorcentajeOcupacion
        // null y pintarlo como 0% lo haría indistinguible de un Gestor CAE que no
        // ha registrado tiempo, así que esa fila no lleva barra y dice por qué.
        CatalogoKpis.OcupacionGestores => _valores!.Bpo.OcupacionPorGestor
            .Select(o => o.PorcentajeOcupacion is { } p
                ? new BarraKpi(o.Nombre, $"{Math.Clamp(p, 0, 100)}%", Math.Clamp(p, 0, 100), ClaseTono(TonoOcupacion(p)))
                : new BarraKpi(o.Nombre, "—", 0, "segmento-neutro", "Sin jornada mensual configurada"))
            .ToList(),
        _ => []
    };

    /// <summary>Recuentos, no porcentajes: el carril lo marca el mayor de la serie, y una barra con algo nunca baja de 2 para que un 1 no desaparezca.</summary>
    private static IReadOnlyList<BarraKpi> Proporcionales(IReadOnlyList<(string Etiqueta, int Valor, string Clase)> filas)
    {
        var maximo = filas.Select(f => f.Valor).DefaultIfEmpty(0).Max();
        return filas.Select(f => new BarraKpi(
            f.Etiqueta,
            f.Valor.ToString(),
            f.Valor <= 0 || maximo <= 0 ? 0 : Math.Max(2, (int)Math.Round(f.Valor * 100.0 / maximo)),
            f.Clase)).ToList();
    }

    /// <summary>Más ocupación que jornada es una señal, no un logro: por encima del 100% va en rojo.</summary>
    private static TonoBadge TonoOcupacion(int porcentaje) => porcentaje switch
    {
        > 100 => TonoBadge.Peligro,
        >= 85 => TonoBadge.Advertencia,
        _ => TonoBadge.Neutro
    };

    /// <summary>
    /// Duplicado a propósito de <c>Incidencias.razor.cs</c>, donde es privado:
    /// el enum no puede llegar a pantalla («MuyGrave»). Unificarlo es un
    /// incremento con sus dos consumidores, no un efecto colateral de este.
    /// </summary>
    private static string EtiquetaGravedad(GravedadIncidencia gravedad) => gravedad switch
    {
        GravedadIncidencia.Leve => "Leve",
        GravedadIncidencia.Grave => "Grave",
        GravedadIncidencia.MuyGrave => "Muy grave",
        _ => gravedad.ToString()
    };

    private static TonoBadge TonoGravedad(GravedadIncidencia gravedad) => gravedad switch
    {
        GravedadIncidencia.Leve => TonoBadge.Advertencia,
        GravedadIncidencia.Grave => TonoBadge.Peligro,
        GravedadIncidencia.MuyGrave => TonoBadge.Peligro,
        _ => TonoBadge.Neutro
    };

    private static string ClaseTono(TonoBadge tono) => tono switch
    {
        TonoBadge.Exito => "segmento-exito",
        TonoBadge.Advertencia => "segmento-advertencia",
        TonoBadge.Peligro => "segmento-peligro",
        _ => "segmento-neutro"
    };

    // ---------------------------------------------------------------- tablas

    /// <param name="Reparto">Porcentaje sobre el tiempo de los Clientes empresariales MOSTRADOS, no sobre el de la cartera entera.</param>
    private sealed record FilaHoras(string Nombre, string Horas, string Reparto, string Desglose);

    /// <summary>
    /// La consulta devuelve solo los cinco Clientes empresariales con más tiempo
    /// (<c>ObtenerKpisBpoQueryHandler.TopClientesPorHoras</c>) y no el total de
    /// la cartera, así que el reparto es sobre lo mostrado y la columna lo dice:
    /// un porcentaje sobre lo que cabe en pantalla presentado como cuota del
    /// total sería falso.
    /// </summary>
    private IReadOnlyList<FilaHoras> FilasHorasPorCliente
    {
        get
        {
            var filas = _valores?.Bpo.HorasPorCliente ?? [];
            var totalMostrado = filas.Sum(f => f.SegundosActivos);

            return filas.Select(f => new FilaHoras(
                f.ClienteNombre,
                Horas(f.SegundosActivos),
                totalMostrado == 0 ? "—" : $"{Math.Round(f.SegundosActivos * 100.0 / totalMostrado)}%",
                totalMostrado == 0
                    ? $"{f.ClienteNombre}: {Horas(f.SegundosActivos)} de gestión medidas."
                    : $"{f.ClienteNombre}: {Horas(f.SegundosActivos)} de las {Horas(totalMostrado)} medidas en los {filas.Count} Clientes empresariales con más tiempo; no incluye el resto de la cartera."))
                .ToList();
        }
    }

    private static string Horas(int segundos) => $"{segundos / 3600.0:F1} h";

    // ---------------------------------------------------------------- vacíos

    /// <summary>
    /// Vacíos distinguidos: cada uno dice exactamente la condición que deja la
    /// serie vacía, sin culpar a una configuración que puede estar bien.
    /// </summary>
    private static string VacioDonut(string codigo) => codigo switch
    {
        CatalogoKpis.DistribucionAntelacion => "Todavía no hay visitas con la antelación medida.",
        _ => "Todavía no hay documentos con fecha de vencimiento que repartir."
    };

    private static string VacioBarras(string codigo) => codigo switch
    {
        CatalogoKpis.IncidenciasPorGravedad => "No hay incidencias registradas.",
        CatalogoKpis.AtribucionUrgencia => "Todavía no hay visitas con la antelación medida.",
        // "Obligatoria" implicaba una norma legal; es configuración
        // (ResolucionTipoDocumentoCentro.Aplica, mismo criterio que Alertas.razor):
        // la fila del centro o, si no dice nada, el valor general del tipo.
        // Y la lista también llega vacía cuando un centro sí la pide pero no
        // tiene trabajadores asignados (CalculoEstadoCentroService cuenta cero
        // requeridos): el texto dice exactamente la condición, sin culpar a la
        // configuración de un centro que puede tenerla bien.
        CatalogoKpis.CentrosConMenorCumplimiento => "Todavía no hay documentación de trabajador que evaluar: ningún centro con trabajadores asignados la pide.",
        _ => "No hay tiempo de gestión registrado este mes. La medición de tiempo se activa en Configuración."
    };

    // ---------------------------------------------------------------- pulso

    private string PulsoVerificaciones => TotalAprobaciones switch
    {
        0 => "Todavía no hay verificaciones de IA resueltas en la organización activa.",
        1 => "1 verificación de IA resuelta en la organización activa.",
        _ => $"{TotalAprobaciones} verificaciones de IA resueltas en la organización activa."
    };

    private string DetallePulsoVerificaciones => TotalAprobaciones == 0
        ? "Ningún Documento verificado por IA en el periodo consultado de la organización activa."
        : $"{TotalAprobaciones} decisiones de verificación resueltas: {_estadisticasAprobacion!.Automaticas} automáticas y {_estadisticasAprobacion.Manuales} manuales. Solo la organización activa.";

    private string DetalleCosteIa => TenantsConPresupuestoIaExcedido.Count == 0
        ? $"{_valores!.CosteIaMesActual:F2} € de coste estimado de IA documental (OCR más extracción) sumando las {Organizaciones} del alcance."
        : $"{_valores!.CosteIaMesActual:F2} € de coste estimado de IA documental (OCR más extracción) sumando las {Organizaciones} del alcance; {TenantsConPresupuestoIaExcedido.Count} por encima de su presupuesto.";

    private string DetalleFacturacion =>
        $"{_valores!.FacturacionEstimadaMesActual:F2} € de facturación estimada, sumando solo los Clientes empresariales con tarifas configuradas de las {Organizaciones} del alcance.";
}
