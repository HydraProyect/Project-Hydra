using System.Globalization;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Dashboard;
using CaeManager.Application.Dashboard.Catalogo;
using CaeManager.Application.Dashboard.Commands;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Domain.Common;
using CaeManager.Domain.Incidencias;
using CaeManager.Domain.Visitas;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DashboardEjecutivoPagina = CaeManager.Web.Features.DashboardEjecutivo.Pages.DashboardEjecutivo;

namespace CaeManager.Web.Tests;

/// <summary>
/// Dashboard Ejecutivo contra su mockup Gen 2 («Dashboard Ejecutivo TALVEG.dc.html»).
///
/// <para>
/// <b>El doble del mediador ejecuta el fan-out REAL.</b>
/// <see cref="ObtenerDashboardEjecutivoQuery"/> la resuelve
/// <see cref="ObtenerDashboardEjecutivoQueryHandler"/> de verdad: pide
/// <see cref="ObtenerClientesAutorizadosQuery"/> y, por cada organización, un
/// <see cref="ObtenerCatalogoKpisQuery"/> dentro de su
/// <see cref="AmbitoTenantExplicito"/>. El doble contesta ese último LEYENDO el
/// ámbito, y una petición sin ámbito explícito revienta: en producción se
/// contaría con el tenant activo. Así las cifras que se afirman aquí son las
/// del merge real —ponderadas por volumen—, no un DTO fabricado a mano que
/// pudiera coincidir con la pantalla por casualidad.
/// </para>
///
/// <para>
/// <b>Lo que NO observa:</b> la resolución real del rol y de la cartera por
/// tenant (eso lo prueba <c>FanOutMultiTenantFugaDeAlcanceTests</c> contra
/// PostgreSQL), la autorización de la ruta (<c>AutorizacionDePaginasTests</c>),
/// la persistencia de la preferencia (<c>GuardarPreferenciaDashboardCommand</c>)
/// ni el aspecto: bUnit no evalúa CSS, así que un tono aquí es una clase, no un
/// color pintado.
/// </para>
/// </summary>
public class DashboardEjecutivoGen2Tests : BunitContext
{
    public DashboardEjecutivoGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly Guid TenantA = Guid.Parse("a1a1a1a1-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("b2b2b2b2-0000-0000-0000-000000000002");

    private const string NombreA = "Refrielectric S.A.";
    private const string NombreB = "Montajes Ebro S.L.";

    private static readonly Guid GestoraMarta = Guid.Parse("cc000000-0000-0000-0000-000000000001");
    private static readonly Guid GestorIker = Guid.Parse("cc000000-0000-0000-0000-000000000002");
    private static readonly Guid ClienteBeroa = Guid.Parse("dd000000-0000-0000-0000-000000000001");
    private static readonly Guid ClienteTuria = Guid.Parse("dd000000-0000-0000-0000-000000000002");

    // ---------------------------------------------------------------- escenario

    private sealed record Organizacion(Guid TenantId, string Nombre, bool EsOrigen, CatalogoKpisValoresDto Valores);

    /// <summary>
    /// Dos organizaciones con volúmenes deliberadamente dispares: cualquier
    /// media sin ponderar daría otro número, así que las cifras que se afirman
    /// distinguen el merge correcto del promedio simple.
    /// </summary>
    private static CatalogoKpisValoresDto ValoresA() => new(
        Documental: new KpisDashboardDto(
            TrabajadoresActivos: 300, Centros: 30,
            DocumentosVencidos: 2, DocumentosUrgentes: 3, DocumentosProximos: 5, DocumentosVigentes: 90,
            VisitasProgramadas: 10, TasaCumplimientoDocumental: 90, VisitasUrgentes: 3),
        TotalDocumentosConVigencia: 100,
        PorcentajeCumplimientoDocumental: 80.0,
        TotalRequeridosCumplimiento: 400,
        CentrosConMenorCumplimiento: [new CentroCumplimientoDto(Guid.NewGuid(), "Centro Norte", 41)],
        IncidenciasAbiertas: 3,
        IncidenciasPorGravedad:
        [
            new GravedadIncidenciaConteoDto(GravedadIncidencia.Leve, 5),
            new GravedadIncidenciaConteoDto(GravedadIncidencia.MuyGrave, 1)
        ],
        TiempoMedioResolucionIncidenciasDias: 2.0,
        TotalIncidenciasResueltas: 10,
        ConfianzaMediaIa: 96.0,
        CosteIaMesActual: 100.50m,
        TiempoMedioProcesamientoIaMs: 2140.0,
        TotalAuditoriasIaMes: 50,
        FacturacionEstimadaMesActual: 1000.00m,
        Bpo: new KpisBpoDto(
            SugerenciasConfirmadasSinEdicion: 30,
            TotalSugerenciasResueltas: 40,
            OcupacionPorGestor: [new OcupacionGestorDto(GestoraMarta, "Marta Ruiz", 309600, 86)],
            HorasPorCliente: [new HorasClienteDto(ClienteBeroa, "Instalaciones Beroa", 149400)],
            DistribucionAntelacion:
            [
                new TramoAntelacionConteoDto(TramoAntelacion.Estandar, 12),
                new TramoAntelacionConteoDto(TramoAntelacion.Urgente, 5)
            ],
            VisitasConFalsoAviso: 5,
            TotalVisitasConAntelacionMedida: 20,
            HorasBloqueadasPorClienteTotal: 600m)
        {
            AtribucionUrgencia =
            [
                new AtribucionUrgenciaConteoDto(AtribucionUrgencia.SinUrgencia, 15),
                new AtribucionUrgenciaConteoDto(AtribucionUrgencia.SolicitudTardiaCliente, 4)
            ]
        },
        PresupuestoMensualIaUsd: 50m);

    private static CatalogoKpisValoresDto ValoresB() => new(
        Documental: new KpisDashboardDto(
            TrabajadoresActivos: 118, Centros: 7,
            DocumentosVencidos: 0, DocumentosUrgentes: 1, DocumentosProximos: 1, DocumentosVigentes: 8,
            VisitasProgramadas: 2, TasaCumplimientoDocumental: 80, VisitasUrgentes: 0),
        TotalDocumentosConVigencia: 10,
        PorcentajeCumplimientoDocumental: 40.0,
        TotalRequeridosCumplimiento: 100,
        CentrosConMenorCumplimiento: [new CentroCumplimientoDto(Guid.NewGuid(), "Nave Berriz", 78)],
        IncidenciasAbiertas: 1,
        IncidenciasPorGravedad:
        [
            new GravedadIncidenciaConteoDto(GravedadIncidencia.Leve, 2),
            new GravedadIncidenciaConteoDto(GravedadIncidencia.Grave, 3)
        ],
        TiempoMedioResolucionIncidenciasDias: 12.0,
        TotalIncidenciasResueltas: 2,
        ConfianzaMediaIa: 60.0,
        CosteIaMesActual: 20.10m,
        TiempoMedioProcesamientoIaMs: 3000.0,
        TotalAuditoriasIaMes: 10,
        FacturacionEstimadaMesActual: 480.00m,
        Bpo: new KpisBpoDto(
            SugerenciasConfirmadasSinEdicion: 7,
            TotalSugerenciasResueltas: 10,
            // Sin jornada mensual configurada: PorcentajeOcupacion llega null.
            OcupacionPorGestor: [new OcupacionGestorDto(GestorIker, "Iker Mendia", 0, null)],
            HorasPorCliente: [new HorasClienteDto(ClienteTuria, "Calderería Turia", 119520)],
            DistribucionAntelacion: [new TramoAntelacionConteoDto(TramoAntelacion.Expres, 3)],
            VisitasConFalsoAviso: 2,
            TotalVisitasConAntelacionMedida: 10,
            HorasBloqueadasPorClienteTotal: 492m)
        {
            AtribucionUrgencia = [new AtribucionUrgenciaConteoDto(AtribucionUrgencia.RetrasoGestor, 1)]
        },
        PresupuestoMensualIaUsd: null);

    private sealed class Escenario
    {
        public List<Organizacion> Organizaciones { get; } =
        [
            new(TenantA, NombreA, true, ValoresA()),
            new(TenantB, NombreB, false, ValoresB()),
        ];

        public Organizacion this[Guid tenantId] => Organizaciones.Single(o => o.TenantId == tenantId);

        /// <summary>Lo que devuelve la preferencia guardada. Por defecto, el catálogo entero: así una cifra mal puesta no se esconde detrás de un KPI no seleccionado.</summary>
        public IReadOnlyList<string> Seleccion { get; set; } = CatalogoKpis.Todos.Select(k => k.Codigo).ToList();

        public EstadisticasAprobacionDocumentoDto Aprobaciones { get; set; } = new(312, 147);

        public IReadOnlyList<RiesgoEmpresaDto> EmpresasEnRiesgo { get; set; } =
        [
            new(Guid.NewGuid(), "Instalaciones Beroa", 8, 3),
            new(Guid.NewGuid(), "Electro Ebro", 0, 1)
        ];

        public Result ResultadoGuardar { get; set; } = Result.Exito();

        /// <summary>Si devuelve una tarea, esa petición se resuelve cuando el test lo diga.</summary>
        public Func<object, Task<object?>?> Retener { get; set; } = _ => null;
    }

    private sealed class MediadorConFanOut(Escenario escenario) : IMediator
    {
        public List<(object Peticion, Guid? Ambito, CancellationToken Token)> Enviadas { get; } = [];

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            var ambito = AmbitoTenantExplicito.TenantIdActual;
            Enviadas.Add((request, ambito, cancellationToken));

            if (escenario.Retener(request) is { } retenida)
                return (TResponse)(await retenida)!;

            object respuesta = request switch
            {
                ObtenerDashboardEjecutivoQuery q => await new ObtenerDashboardEjecutivoQueryHandler(this).Handle(q, cancellationToken),
                ObtenerClientesAutorizadosQuery => escenario.Organizaciones
                    .Select(o => new ClienteAutorizadoDto(o.TenantId, o.Nombre, o.EsOrigen)).ToList(),
                ObtenerCatalogoKpisQuery => escenario[ambito
                    ?? throw new InvalidOperationException("KPIs de una organización pedidos sin ámbito explícito: se contarían con el tenant activo.")]
                    .Valores,
                ObtenerPreferenciaDashboardQuery => escenario.Seleccion,
                ObtenerEstadisticasAprobacionDocumentoQuery => escenario.Aprobaciones,
                ObtenerDesgloseDashboardQuery => new DesgloseDashboardDto([], [], escenario.EmpresasEnRiesgo),
                GuardarPreferenciaDashboardCommand => escenario.ResultadoGuardar,
                _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")
            };
            return (TResponse)respuesta;
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class LoggerQueGuarda : ILogger<DashboardEjecutivoPagina>
    {
        public List<string> Errores { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error) Errores.Add(formatter(state, exception));
        }
    }

    // ---------------------------------------------------------------- arnés

    private sealed record Montaje(IRenderedComponent<DashboardEjecutivoPagina> Cut, MediadorConFanOut Mediador, LoggerQueGuarda Logger, ToastService Toasts);

    private Montaje Renderizar(Escenario escenario)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-ES");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("es-ES");

        var mediador = new MediadorConFanOut(escenario);
        var logger = new LoggerQueGuarda();
        var toasts = new ToastService();
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped(_ => toasts);
        Services.AddSingleton<ILogger<DashboardEjecutivoPagina>>(logger);

        return new Montaje(Render<DashboardEjecutivoPagina>(), mediador, logger, toasts);
    }

    private static string Texto(IElement elemento) => elemento.TextContent.Trim();

    private static string TextoNormalizado(IElement elemento) =>
        string.Join(' ', elemento.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Lo que la <c>VentanaContexto</c> enseña siempre, sin el panel del
    /// desglose: <c>TextContent</c> incluiría los dos, y una aserción sobre el
    /// disparador se cumpliría con el texto del detalle.
    /// </summary>
    private static string Disparador(IElement ventana)
    {
        var panel = ventana.QuerySelector(".ventana-contexto-panel");
        return string.Concat(ventana.ChildNodes.Where(n => !ReferenceEquals(n, panel)).Select(n => n.TextContent)).Trim();
    }

    private static string TextoPagina(IRenderedComponent<DashboardEjecutivoPagina> cut) =>
        string.Join(' ', cut.Find(".contenedor-pagina").TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static (string Etiqueta, string Valor, string Pista, string Clase) Tile(IElement tarjeta) => (
        Texto(tarjeta.QuerySelector(".tarjeta-metrica-etiqueta")!),
        Texto(tarjeta.QuerySelector(".tarjeta-metrica-valor")!),
        tarjeta.QuerySelector(".tarjeta-metrica-pista") is { } pista ? Texto(pista) : string.Empty,
        tarjeta.ClassList.Single(c => c.StartsWith("tarjeta-metrica-", StringComparison.Ordinal)));

    private static (string Etiqueta, string Valor, string Pista, string Clase) TilePorEtiqueta(
        IRenderedComponent<DashboardEjecutivoPagina> cut, string etiqueta) =>
        cut.FindAll(".rejilla-kpis-criticos .tarjeta-metrica").Select(Tile).Single(t => t.Etiqueta == etiqueta);

    /// <summary>La tarjeta de un KPI de gráfico o tabla, buscada por su título.</summary>
    private static IElement TarjetaKpi(IRenderedComponent<DashboardEjecutivoPagina> cut, string titulo) =>
        cut.FindAll(".tarjeta-kpi").Single(t => Texto(t.QuerySelector(".tarjeta-titulo")!) == titulo);

    private static IReadOnlyList<(string Etiqueta, string Valor, string Clase)> Leyenda(IElement tarjeta) =>
        tarjeta.QuerySelectorAll(".leyenda-donut-item")
            .Select(i => (
                Texto(i.QuerySelector(".leyenda-donut-etiqueta")!),
                Disparador(i.QuerySelector(".leyenda-donut-valor")!),
                i.QuerySelector(".leyenda-donut-punto")!.ClassList.Single(c => c.StartsWith("segmento-", StringComparison.Ordinal))))
            .ToList();

    private static IReadOnlyList<(string Etiqueta, string Valor, string Ancho)> Barras(IElement tarjeta) =>
        tarjeta.QuerySelectorAll(".barra-kpi")
            .Select(b => (
                Texto(b.QuerySelector(".barra-kpi-etiqueta")!),
                Texto(b.QuerySelector(".barra-kpi-valor") ?? b.QuerySelector(".barra-kpi-sin-dato")!),
                b.QuerySelector(".barra-kpi-relleno")?.GetAttribute("style") ?? string.Empty))
            .ToList();

    /// <summary>Celdas de cada fila; la de un recuento con desglose se lee por su disparador, no por el panel.</summary>
    private static IReadOnlyList<IReadOnlyList<string>> Filas(IElement tarjeta) =>
        tarjeta.QuerySelectorAll("tbody tr")
            .Select(f => (IReadOnlyList<string>)f.QuerySelectorAll("td")
                .Select(c => c.QuerySelector(".ventana-contexto") is { } ventana ? Disparador(ventana) : Texto(c))
                .ToList())
            .ToList();

    private static Task Pulsar(IRenderedComponent<DashboardEjecutivoPagina> cut, string texto) =>
        cut.FindAll("button").Single(b => Texto(b) == texto).ClickAsync(new MouseEventArgs());

    /// <summary>
    /// Con algo guardado el panel arranca colapsado (solo se abre de entrada
    /// cuando no hay nada elegido), así que hay que abrirlo antes de tocar sus
    /// casillas: si no, no existen en el DOM.
    /// </summary>
    private static Task AbrirPersonalizar(IRenderedComponent<DashboardEjecutivoPagina> cut) =>
        cut.Find(".seccion-colapsable-cabecera").ClickAsync(new MouseEventArgs());

    private static Task Marcar(IRenderedComponent<DashboardEjecutivoPagina> cut, string titulo, bool valor)
    {
        var etiqueta = cut.FindAll(".checkbox-kpi").Single(l => Texto(l) == titulo);
        return etiqueta.QuerySelector("input")!.ChangeAsync(new ChangeEventArgs { Value = valor });
    }

    // ---------------------------------------------------------------- fan-out y alcance

    [Fact]
    public void La_pantalla_no_fija_ningun_ambito_y_cada_organizacion_se_cuenta_en_el_suyo()
    {
        var montaje = Renderizar(new Escenario());

        var enviadas = montaje.Mediador.Enviadas;
        enviadas.Where(e => e.Peticion is ObtenerDashboardEjecutivoQuery).Should().ContainSingle()
            .Which.Ambito.Should().BeNull("el reparto por organización lo hace la consulta, no la pantalla");
        enviadas.Where(e => e.Peticion is ObtenerCatalogoKpisQuery).Select(e => e.Ambito).Should().Equal(
            [TenantA, TenantB], "una vuelta por organización autorizada, cada una con su ámbito");
        enviadas.Should().OnlyContain(e => e.Token.CanBeCanceled,
            "todas las llamadas de la carga llevan el token del ciclo de la página, también las cuatro que lanza la pantalla");
    }

    [Fact]
    public async Task Las_categorias_del_panel_se_leen_en_castellano()
    {
        var cut = Renderizar(new Escenario()).Cut;
        await AbrirPersonalizar(cut);

        cut.FindAll(".categoria-kpi-titulo").Select(Texto).Should().Equal(
            ["Documental", "Incidencias", "IA", "Facturación", "Operativa", "Fricción"],
            "«Ia» y «Facturacion» son nombres de miembros del enum, no rótulos");
    }

    /// <summary>
    /// Las cifras del panel son las del merge ponderado por volumen. El promedio
    /// simple entre las dos organizaciones daría otro número en los tres casos,
    /// así que la aserción distingue un merge del otro.
    /// </summary>
    [Fact]
    public void Las_medias_del_panel_estan_ponderadas_por_volumen_y_no_promediadas_entre_organizaciones()
    {
        var cut = Renderizar(new Escenario()).Cut;

        // 98 vigentes de 110 con vigencia = 89%; la media simple de 90 y 80 sería 85.
        TilePorEtiqueta(cut, "Tasa de cumplimiento documental").Should().Be(
            ("Tasa de cumplimiento documental", "89%", "Ponderada por el volumen de documentos de cada organización", "tarjeta-metrica-advertencia"));

        // (80×400 + 40×100) / 500 = 72; la media simple sería 60.
        TilePorEtiqueta(cut, "% de cumplimiento documental (trabajadores)").Valor.Should().Be("72%");

        // (96×50 + 60×10) / 60 = 90; la media simple sería 78.
        TilePorEtiqueta(cut, "Confianza media de extracción IA").Should().Be(
            ("Confianza media de extracción IA", "90%", "Ponderada por las extracciones de cada organización", "tarjeta-metrica-exito"));

        // (2×10 + 12×2) / 12 = 3,67 días; la media simple sería 7.
        TilePorEtiqueta(cut, "Tiempo medio de resolución").Valor.Should().Be("3,7 días");
    }

    [Fact]
    public void Los_recuentos_del_panel_suman_las_dos_organizaciones()
    {
        var cut = Renderizar(new Escenario()).Cut;

        TilePorEtiqueta(cut, "Trabajadores activos").Valor.Should().Be("418");
        TilePorEtiqueta(cut, "Centros").Valor.Should().Be("37");
        TilePorEtiqueta(cut, "Visitas programadas").Valor.Should().Be("12");
        TilePorEtiqueta(cut, "Incidencias abiertas").Valor.Should().Be("4");
        TilePorEtiqueta(cut, "Facturación estimada del mes").Should().Be(
            ("Facturación estimada del mes", "1480,00 €", "Solo los Clientes empresariales con tarifas configuradas", "tarjeta-metrica-neutro"));
    }

    /// <summary>
    /// Dos consultas de la pantalla no recorren las demás organizaciones —se
    /// piden sin <see cref="AmbitoTenantExplicito"/>, así que responden con el
    /// tenant activo—. Con más de una organización en el alcance, lo que sale de
    /// ellas se rotula; con una sola no hay nada que distinguir y no se rotula.
    /// </summary>
    [Fact]
    public void Lo_que_solo_mide_la_organizacion_activa_lo_dice_cuando_hay_varias()
    {
        const string aviso = "Solo la organización activa: esta consulta no recorre las demás.";

        var cut = Renderizar(new Escenario()).Cut;

        Texto(TarjetaKpi(cut, "Gestiones automáticas vs manuales").QuerySelector(".aviso-alcance-kpi")!).Should().Be(aviso);
        Texto(TarjetaKpi(cut, "Empresas con más riesgo").QuerySelector(".aviso-alcance-kpi")!).Should().Be(aviso);
        Disparador(cut.Find(".pulso-negocio-frase")).Should().Be("459 verificaciones de IA resueltas en la organización activa.");
    }

    [Fact]
    public void Con_una_sola_organizacion_no_hay_dos_alcances_que_distinguir()
    {
        var escenario = new Escenario();
        escenario.Organizaciones.RemoveAll(o => o.TenantId == TenantB);

        var cut = Renderizar(escenario).Cut;

        cut.FindAll(".aviso-alcance-kpi").Should().BeEmpty();
        Disparador(cut.Find(".periodo-texto")).Should().EndWith("· 1 organización");
    }

    [Fact]
    public void El_periodo_dice_el_rango_consultado_y_cuantas_organizaciones_suma()
    {
        var cut = Renderizar(new Escenario()).Cut;

        var hoy = DateTime.UtcNow;
        var mes = new DateOnly(hoy.Year, hoy.Month, 1);
        var ultimoDia = mes.AddMonths(1).AddDays(-1);
        var esperado = $"Del {mes:d 'de' MMMM 'de' yyyy} al {ultimoDia:d 'de' MMMM 'de' yyyy} · 2 organizaciones";

        var periodo = cut.Find(".periodo-texto");
        Disparador(periodo).Should().Be(esperado, "el mes en curso se consulta entero: PeriodoKpi es semiabierto y su fin es exclusivo");
        periodo.GetAttribute("aria-label").Should().Be(
            "2 organizaciones en el alcance: la tuya más las que os han delegado su gestión CAE. Los KPI de esta sección suman las 2.");
    }

    // ---------------------------------------------------------------- gráficos

    [Fact]
    public void El_semaforo_documental_reparte_el_anillo_entero_y_pone_cada_cifra_en_texto()
    {
        var cut = Renderizar(new Escenario()).Cut;
        var tarjeta = TarjetaKpi(cut, "Semáforo documental");

        Leyenda(tarjeta).Should().Equal(
            ("Vigente", "98 (89%)", "segmento-exito"),
            ("Próximo", "6 (5%)", "segmento-advertencia"),
            ("Urgente", "4 (4%)", "segmento-advertencia-fuerte"),
            ("Vencido", "2 (2%)", "segmento-peligro"));
        Texto(tarjeta.QuerySelector(".grafico-total")!).Should().Be("110 documentos con vigencia en total");
        Texto(tarjeta.QuerySelector(".donut-total")!).Should().Be("110");

        // Los arcos reparten la circunferencia entera: sin rendija que insinúe
        // una quinta categoría inexistente.
        var arcos = tarjeta.QuerySelectorAll("circle.donut-arco");
        arcos.Should().HaveCount(4);
        arcos.Sum(a => int.Parse(a.GetAttribute("stroke-dasharray")!.Split(' ')[0], CultureInfo.InvariantCulture))
            .Should().Be(390);
        arcos.Select(a => a.GetAttribute("stroke-dasharray")!).Should().OnlyContain(
            d => !d.Contains(','), "un decimal con coma de es-ES dejaría el atributo inválido");

        tarjeta.QuerySelector("svg.donut")!.GetAttribute("aria-hidden").Should().Be(
            "true", "lo que dice el anillo lo dice la leyenda en texto");
    }

    [Fact]
    public void La_leyenda_del_donut_abre_el_desglose_literal_de_cada_segmento()
    {
        var cut = Renderizar(new Escenario()).Cut;

        var valores = TarjetaKpi(cut, "Semáforo documental").QuerySelectorAll(".leyenda-donut-valor");
        valores[0].GetAttribute("aria-label").Should().Be("Vigente: 98 de 110 documentos con vigencia, el 89% del total.");
        valores[0].GetAttribute("tabindex").Should().Be("0", "el desglose se abre también con el foco, no solo con el puntero");
        valores[3].GetAttribute("aria-label").Should().Be("Vencido: 2 de 110 documentos con vigencia, el 2% del total.");
    }

    [Fact]
    public void Las_barras_de_recuento_escalan_al_mayor_de_la_serie_y_no_ensenan_el_nombre_del_enum()
    {
        var cut = Renderizar(new Escenario()).Cut;

        Barras(TarjetaKpi(cut, "Incidencias por gravedad")).Should().Equal(
            ("Leve", "7", "width:100%"),
            ("Grave", "3", "width:43%"),
            ("Muy grave", "1", "width:14%"));
        TextoPagina(cut).Should().NotContain("MuyGrave", "el nombre del miembro del enum no llega a pantalla");
    }

    [Fact]
    public void Las_barras_de_porcentaje_usan_el_cien_por_cien_como_carril_y_el_tono_del_semaforo()
    {
        var cut = Renderizar(new Escenario()).Cut;
        var tarjeta = TarjetaKpi(cut, "Centros con menor cumplimiento");

        Barras(tarjeta).Should().Equal(
            ($"{NombreA} · Centro Norte", "41%", "width:41%"),
            ($"{NombreB} · Nave Berriz", "78%", "width:78%"));
        tarjeta.QuerySelectorAll(".barra-kpi-relleno")
            .Select(r => r.ClassList.Single(c => c.StartsWith("segmento-", StringComparison.Ordinal)))
            .Should().Equal("segmento-peligro", "segmento-advertencia");
    }

    /// <summary>
    /// <see cref="OcupacionGestorDto.PorcentajeOcupacion"/> es nullable: sin
    /// jornada mensual configurada no hay ocupación que calcular. Dibujarlo como
    /// 0% lo haría indistinguible de un Gestor CAE que no ha registrado tiempo.
    /// </summary>
    [Fact]
    public void Una_ocupacion_sin_jornada_configurada_no_se_dibuja_como_cero()
    {
        var cut = Renderizar(new Escenario()).Cut;
        var tarjeta = TarjetaKpi(cut, "Ocupación por Gestor CAE");

        Barras(tarjeta).Should().Equal(
            ("Marta Ruiz", "86%", "width:86%"),
            ("Iker Mendia", "Sin jornada mensual configurada", string.Empty));
        tarjeta.QuerySelectorAll(".barra-kpi")[1].QuerySelectorAll(".barra-kpi-relleno").Should().BeEmpty(
            "sin dato no hay barra: una barra vacía y un 0% se leen igual");
    }

    // ---------------------------------------------------------------- tablas

    /// <summary>
    /// La consulta devuelve solo los cinco Clientes empresariales con más tiempo
    /// (<c>ObtenerKpisBpoQueryHandler.TopClientesPorHoras</c>), no el total de la
    /// cartera: el reparto es sobre lo mostrado y la columna y el desglose lo
    /// dicen. Presentarlo como cuota del total sería un porcentaje calculado
    /// sobre lo que cabe en pantalla.
    /// </summary>
    [Fact]
    public void El_reparto_de_horas_dice_que_es_sobre_los_mostrados_y_no_sobre_la_cartera()
    {
        var cut = Renderizar(new Escenario()).Cut;
        var tarjeta = TarjetaKpi(cut, "Horas de gestión por Cliente empresarial");

        tarjeta.QuerySelectorAll("thead th").Select(Texto).Should().Equal(
            "Cliente empresarial", "Horas de gestión", "Reparto entre los mostrados");
        Filas(tarjeta).Should().BeEquivalentTo(
            new[]
            {
                new[] { "Instalaciones Beroa", "41,5 h", "56%" },
                new[] { "Calderería Turia", "33,2 h", "44%" }
            },
            o => o.WithStrictOrdering());
        tarjeta.QuerySelectorAll(".reparto-horas")[0].GetAttribute("aria-label").Should().Be(
            "Instalaciones Beroa: 41,5 h de las 74,7 h medidas en los 2 Clientes empresariales con más tiempo; no incluye el resto de la cartera.");
    }

    [Fact]
    public void Las_empresas_con_mas_riesgo_enlazan_su_busqueda_y_un_cero_no_es_una_senal()
    {
        var cut = Renderizar(new Escenario()).Cut;
        var tarjeta = TarjetaKpi(cut, "Empresas con más riesgo");

        var enlaces = tarjeta.QuerySelectorAll("a.nombre-empresa-riesgo");
        enlaces.Select(Texto).Should().Equal("Instalaciones Beroa", "Electro Ebro");
        enlaces[0].GetAttribute("href").Should().Be("/empresas?q=Instalaciones%20Beroa",
            "el deep link sigue siendo por texto y no por Id, y ahora se alcanza con el teclado");

        var badgesPrimera = tarjeta.QuerySelectorAll("tbody tr")[0].QuerySelectorAll(".badge");
        badgesPrimera[0].GetAttribute("title").Should().Be("Instalaciones Beroa: 8 documentos de trabajador vencidos");
        badgesPrimera[0].ClassList.Should().Contain("badge-peligro");
        tarjeta.QuerySelectorAll("tbody tr")[1].QuerySelectorAll(".badge")[0].ClassList.Should().Contain(
            "badge-neutro", "un cero no es una señal de peligro");
    }

    // ---------------------------------------------------------------- presupuesto de IA

    /// <summary>
    /// Vacío no distingue «todas dentro de su presupuesto» de «ninguna tiene
    /// presupuesto configurado»: sin excedidos la tile no afirma nada, ni en la
    /// pista ni en el tono.
    /// </summary>
    [Fact]
    public void Con_una_organizacion_por_encima_de_su_presupuesto_la_tile_avisa_y_la_nombra()
    {
        var cut = Renderizar(new Escenario()).Cut;

        TilePorEtiqueta(cut, "Coste IA del mes").Should().Be(
            ("Coste IA del mes", "120,60 €", "1 organización sobre su presupuesto de IA", "tarjeta-metrica-peligro"));
        TextoNormalizado(cut.Find(".alerta-formulario")).Should().StartWith(
            $"Presupuesto de IA superado este período en: {NombreA}.");
    }

    /// <summary>
    /// Vacío no distingue «todas dentro de su presupuesto» de «ninguna tiene
    /// presupuesto configurado»: sin excedidos la tile no afirma nada, ni en la
    /// pista ni en el tono.
    /// </summary>
    [Fact]
    public void Sin_ninguna_organizacion_por_encima_el_presupuesto_de_IA_no_afirma_nada()
    {
        var escenario = new Escenario();
        escenario.Organizaciones[0] = escenario.Organizaciones[0] with
        {
            Valores = ValoresA() with { PresupuestoMensualIaUsd = 500m }
        };

        var cut = Renderizar(escenario).Cut;

        TilePorEtiqueta(cut, "Coste IA del mes").Should().Be(
            ("Coste IA del mes", "120,60 €", string.Empty, "tarjeta-metrica-neutro"));
        cut.FindAll(".alerta-formulario").Should().BeEmpty();
    }

    [Fact]
    public void El_tono_de_los_falsos_avisos_esta_invertido_y_la_pista_lo_dice()
    {
        var cut = Renderizar(new Escenario()).Cut;

        // 7 de 30 visitas medidas = 23%: un 23% alto se pinta en ámbar, no en verde.
        TilePorEtiqueta(cut, "Falsos avisos con tiempo").Should().Be(
            ("Falsos avisos con tiempo", "23%", "Tono invertido: un porcentaje alto es el problema", "tarjeta-metrica-advertencia"));
    }

    // ---------------------------------------------------------------- personalizar

    [Fact]
    public void El_resumen_cuenta_los_KPI_que_se_pintan_y_no_los_codigos_guardados()
    {
        var escenario = new Escenario
        {
            // Un código que ya no existe en el catálogo: la consulta lo filtra,
            // y aquí tampoco puede contar como mostrado.
            Seleccion = [CatalogoKpis.TrabajadoresActivos, CatalogoKpis.Centros, "doc.kpi-que-ya-no-existe"]
        };

        var cut = Renderizar(escenario).Cut;

        Texto(cut.Find(".resumen-seleccion")).Should().Be($"2 de {CatalogoKpis.Todos.Count} KPI mostrados");
        cut.FindAll(".rejilla-kpis-criticos .tarjeta-metrica").Should().HaveCount(2);
    }

    [Fact]
    public async Task Marcar_no_aplica_nada_hasta_guardar_y_mientras_lo_dice()
    {
        var escenario = new Escenario { Seleccion = [CatalogoKpis.TrabajadoresActivos] };
        var (cut, mediador, _, _) = Renderizar(escenario);
        await AbrirPersonalizar(cut);

        cut.FindAll(".aviso-sin-guardar").Should().BeEmpty("nada marcado de más: no hay cambios pendientes");

        await Marcar(cut, "Centros", true);

        Texto(cut.Find(".aviso-sin-guardar")).Should().Be("Cambios sin guardar");
        cut.FindAll(".rejilla-kpis-criticos .tarjeta-metrica").Should().HaveCount(1, "marcar no pinta el KPI: lo pinta guardar");

        await Pulsar(cut, "Guardar selección");

        cut.FindAll(".aviso-sin-guardar").Should().BeEmpty();
        cut.FindAll(".rejilla-kpis-criticos .tarjeta-metrica").Select(t => Tile(t).Etiqueta).Should().Equal(
            "Trabajadores activos", "Centros");
        mediador.Enviadas.Where(e => e.Peticion is GuardarPreferenciaDashboardCommand)
            .Select(e => ((GuardarPreferenciaDashboardCommand)e.Peticion).CodigosKpi)
            .Should().ContainSingle().Which.Should().Equal(CatalogoKpis.TrabajadoresActivos, CatalogoKpis.Centros);
    }

    /// <summary>
    /// Desenlace honesto: un comando fallido no puede dejar la pantalla diciendo
    /// que se guardó. Lo guardado sigue siendo lo de antes y el aviso de cambios
    /// pendientes sigue puesto.
    /// </summary>
    [Fact]
    public async Task Si_el_guardado_falla_no_se_declara_guardado_nada()
    {
        var escenario = new Escenario
        {
            Seleccion = [CatalogoKpis.TrabajadoresActivos],
            ResultadoGuardar = Result.Fallo(Error.Crear("PreferenciaDashboard.Invalida", "Selecciona al menos un KPI."))
        };
        var (cut, _, _, toasts) = Renderizar(escenario);
        await AbrirPersonalizar(cut);

        await Marcar(cut, "Centros", true);
        await Pulsar(cut, "Guardar selección");

        toasts.Mensajes.Select(m => (m.Mensaje, m.Tono)).Should().Equal(
            ("Selecciona al menos un KPI.", TonoToast.Error));
        Texto(cut.Find(".aviso-sin-guardar")).Should().Be("Cambios sin guardar");
        cut.FindAll(".rejilla-kpis-criticos .tarjeta-metrica").Select(t => Tile(t).Etiqueta).Should().Equal(
            "Trabajadores activos");
    }

    /// <summary>
    /// Se guarda lo que estaba marcado AL PULSAR. Si el usuario marca otro KPI
    /// mientras el comando viaja, ese no se envió: declararlo guardado dejaría la
    /// pantalla afirmando una preferencia que el servidor no tiene.
    /// </summary>
    [Fact]
    public async Task Lo_que_se_declara_guardado_es_lo_que_se_envio_y_no_lo_marcado_despues()
    {
        var respuesta = new TaskCompletionSource<object?>();
        var escenario = new Escenario
        {
            Seleccion = [CatalogoKpis.TrabajadoresActivos],
            Retener = p => p is GuardarPreferenciaDashboardCommand ? respuesta.Task : null
        };
        var (cut, mediador, _, _) = Renderizar(escenario);
        await AbrirPersonalizar(cut);

        await Marcar(cut, "Centros", true);
        // Sin await: el clic no vuelve hasta que el comando responda, y el
        // sentido del test es marcar OTRO KPI mientras sigue en vuelo.
        var guardado = Pulsar(cut, "Guardar selección");
        await Marcar(cut, "Visitas programadas", true);

        await cut.InvokeAsync(() => respuesta.SetResult(Result.Exito()));
        await guardado;

        cut.WaitForAssertion(() => cut.FindAll(".rejilla-kpis-criticos .tarjeta-metrica").Should().HaveCount(2));
        cut.FindAll(".rejilla-kpis-criticos .tarjeta-metrica").Select(t => Tile(t).Etiqueta).Should().Equal(
            "Trabajadores activos", "Centros");
        Texto(cut.Find(".aviso-sin-guardar")).Should().Be(
            "Cambios sin guardar", "«Visitas programadas» quedó marcado y nunca se envió");
        mediador.Enviadas.Where(e => e.Peticion is GuardarPreferenciaDashboardCommand)
            .Select(e => ((GuardarPreferenciaDashboardCommand)e.Peticion).CodigosKpi.Count)
            .Should().Equal(2);
    }

    /// <summary>
    /// Guarda de reentrada al ENTRAR: el botón deshabilitado no basta, porque el
    /// clic ya viajaba cuando se deshabilitó. Dos clics seguidos sobre el mismo
    /// guardado en vuelo envían un solo comando.
    /// </summary>
    [Fact]
    public async Task Dos_clics_sobre_Guardar_envian_un_solo_comando()
    {
        var respuesta = new TaskCompletionSource<object?>();
        var escenario = new Escenario
        {
            Seleccion = [CatalogoKpis.TrabajadoresActivos],
            Retener = p => p is GuardarPreferenciaDashboardCommand ? respuesta.Task : null
        };
        var (cut, mediador, _, _) = Renderizar(escenario);
        await AbrirPersonalizar(cut);

        // Ninguno de los dos se espera: el primero no vuelve hasta que el
        // comando responda, y el segundo llega con el primero en vuelo — que es
        // exactamente el clic que ya viajaba cuando el botón se deshabilitó.
        var primero = Pulsar(cut, "Guardar selección");
        var segundo = Pulsar(cut, "Guardar selección");

        mediador.Enviadas.Count(e => e.Peticion is GuardarPreferenciaDashboardCommand).Should().Be(1);

        await cut.InvokeAsync(() => respuesta.SetResult(Result.Exito()));
        await primero;
        await segundo;
    }

    // ---------------------------------------------------------------- estados

    [Fact]
    public void Sin_ningun_KPI_elegido_la_pantalla_solo_ofrece_el_panel_de_Personalizar()
    {
        var cut = Renderizar(new Escenario { Seleccion = [] }).Cut;

        Texto(cut.Find(".estado-vacio h3")).Should().Be("Todavía no has elegido ningún KPI");
        cut.FindAll(".tarjeta-metrica").Should().BeEmpty();
        cut.FindAll(".tarjeta-kpi").Should().BeEmpty();
        cut.FindAll(".pulso-negocio").Should().BeEmpty("un pulso con cifras que nadie eligió contradice «los KPI que elijas»");
        cut.Find(".seccion-colapsable-contenido").Should().NotBeNull("sin selección, el panel arranca abierto");
    }

    [Fact]
    public void Cada_serie_vacia_dice_su_propia_condicion()
    {
        var escenario = new Escenario();
        for (var i = 0; i < escenario.Organizaciones.Count; i++)
        {
            var organizacion = escenario.Organizaciones[i];
            escenario.Organizaciones[i] = organizacion with
            {
                Valores = organizacion.Valores with
                {
                    CentrosConMenorCumplimiento = [],
                    IncidenciasPorGravedad = [],
                    Bpo = KpisBpoDto.Vacio
                }
            };
        }
        escenario.Aprobaciones = new EstadisticasAprobacionDocumentoDto(0, 0);
        escenario.EmpresasEnRiesgo = [];

        var cut = Renderizar(escenario).Cut;

        string Vacio(string titulo) => Texto(TarjetaKpi(cut, titulo).QuerySelector("p.texto-vacio-seccion")!);

        Vacio("Centros con menor cumplimiento").Should().Be(
            "Todavía no hay documentación de trabajador que evaluar: ningún centro con trabajadores asignados la pide.");
        Vacio("Incidencias por gravedad").Should().Be("No hay incidencias registradas.");
        Vacio("Distribución por tramo de antelación").Should().Be("Todavía no hay visitas con la antelación medida.");
        Vacio("Urgencias por atribución").Should().Be("Todavía no hay visitas con la antelación medida.");
        Vacio("Ocupación por Gestor CAE").Should().Be(
            "No hay tiempo de gestión registrado este mes. La medición de tiempo se activa en Configuración.");
        Vacio("Horas de gestión por Cliente empresarial").Should().Be(
            "No hay tiempo de gestión registrado este mes. La medición de tiempo se activa en Configuración.");
        Vacio("Empresas con más riesgo").Should().Be("Ninguna empresa tiene documentación urgente o vencida.");
        Vacio("Gestiones automáticas vs manuales").Should().Be("Todavía no hay documentos verificados por IA.");

        // Sin resoluciones no hay porcentaje que enseñar: un 0% mentiría.
        TilePorEtiqueta(cut, "Índice de palanca IA").Valor.Should().Be("—");
        TilePorEtiqueta(cut, "Falsos avisos con tiempo").Valor.Should().Be("—");
        Disparador(cut.Find(".pulso-negocio-frase")).Should().Be(
            "Todavía no hay verificaciones de IA resueltas en la organización activa.");
    }

    [Fact]
    public async Task Si_la_carga_falla_se_puede_reintentar()
    {
        var escenario = new Escenario();
        var fallos = 1;
        escenario.Retener = p => p is ObtenerDashboardEjecutivoQuery && fallos-- > 0
            ? Task.FromException<object?>(new InvalidOperationException("caída de prueba"))
            : null;

        var (cut, mediador, logger, _) = Renderizar(escenario);

        Texto(cut.Find(".estado-vacio h3")).Should().Be("No pudimos cargar el Dashboard Ejecutivo");
        cut.FindAll(".tarjeta-metrica").Should().BeEmpty();
        logger.Errores.Should().ContainSingle();

        await Pulsar(cut, "Reintentar");

        cut.FindAll(".estado-vacio").Should().BeEmpty();
        TilePorEtiqueta(cut, "Trabajadores activos").Valor.Should().Be("418");
        mediador.Enviadas.Count(e => e.Peticion is ObtenerDashboardEjecutivoQuery).Should().Be(2);
    }

    // ---------------------------------------------------------------- carreras y retirada

    /// <summary>
    /// Salir de la página cancela la consulta en curso, y su respuesta tardía ya
    /// no toca un componente retirado. Que el token quede cancelado demuestra
    /// además que el Dispose se ejecutó de verdad: DisposeComponentsAsync lo
    /// llama, cut.Dispose() de bUnit no.
    /// </summary>
    [Fact]
    public async Task Salir_de_la_pagina_cancela_la_carga_en_curso()
    {
        var respuesta = new TaskCompletionSource<object?>();
        var escenario = new Escenario { Retener = p => p is ObtenerDashboardEjecutivoQuery ? respuesta.Task : null };
        var (cut, mediador, _, _) = Renderizar(escenario);
        cut.FindAll(".esqueleto-lista").Should().ContainSingle("mientras la consulta no vuelve, se pinta la carga");

        var tokens = mediador.Enviadas.Select(e => e.Token).ToList();
        tokens.Should().OnlyContain(t => t.CanBeCanceled, "todas las llamadas llevan el token del ciclo de la página");
        tokens.Should().OnlyContain(t => !t.IsCancellationRequested);

        await DisposeComponentsAsync();

        tokens.Should().OnlyContain(t => t.IsCancellationRequested, "salir de la página cancela lo que siga en vuelo");
        var llegaTarde = () => cut.InvokeAsync(() => respuesta.SetResult(null));
        await llegaTarde.Should().NotThrowAsync("la respuesta tardía no toca un componente retirado");
    }

    /// <summary>
    /// Un fallo que vuelve después de salir —lo normal: la cancelación llega como
    /// excepción— no es un error de la pantalla: no se registra ni prepara un «No
    /// pudimos cargar» que nadie va a ver.
    /// </summary>
    [Fact]
    public async Task Un_fallo_que_llega_tras_salir_no_se_registra_como_error()
    {
        var respuesta = new TaskCompletionSource<object?>();
        var escenario = new Escenario { Retener = p => p is ObtenerDashboardEjecutivoQuery ? respuesta.Task : null };
        var (cut, _, logger, _) = Renderizar(escenario);

        await DisposeComponentsAsync();
        await cut.InvokeAsync(() => respuesta.SetException(new OperationCanceledException("cancelada al salir")));

        logger.Errores.Should().BeEmpty("la carga ya no es de nadie: su fallo no es un error que registrar");
    }

    /// <summary>
    /// Cambiar de periodo establece un contexto nuevo. Lo que vuelva de la carga
    /// anterior no puede pintarse, ni siquiera para declarar un error, y la
    /// bandera de guardado del contexto retirado no reabre el nuevo.
    /// </summary>
    [Fact]
    public async Task Lo_que_vuelve_de_una_carga_retirada_no_pinta_nada()
    {
        var primera = new TaskCompletionSource<object?>();
        var retener = true;
        var escenario = new Escenario();
        escenario.Retener = p => p is ObtenerDashboardEjecutivoQuery && retener ? primera.Task : null;
        var (cut, mediador, logger, _) = Renderizar(escenario);

        retener = false;
        await cut.Find(".dashboard-periodo select").ChangeAsync(new ChangeEventArgs { Value = "MesAnterior" });

        cut.WaitForAssertion(() => TilePorEtiqueta(cut, "Trabajadores activos").Valor.Should().Be("418"));
        var visibleTrasLaSegunda = TextoPagina(cut);

        await cut.InvokeAsync(() => primera.SetException(new InvalidOperationException("carga retirada")));

        logger.Errores.Should().BeEmpty("el fallo de una carga que ya no es la vigente no es un error de la pantalla");
        cut.FindAll(".estado-vacio").Should().BeEmpty();
        TextoPagina(cut).Should().Be(visibleTrasLaSegunda);
        mediador.Enviadas.Count(e => e.Peticion is ObtenerDashboardEjecutivoQuery).Should().Be(2);
    }
}
