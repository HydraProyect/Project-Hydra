using Bunit;
using CaeManager.Application.Calendario.Queries;
using CaeManager.Application.Visitas.Queries.ObtenerVisitasParaCalendario;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Calendario.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Calendario Gen 2. Lo que se prueba es lo que la pantalla AFIRMA de cada día
/// —recuento, peor estado, visitas— y lo que conserva del código anterior que
/// el mockup omitía (estado de notificación, número de trabajadores, visitas
/// de varios días), no la maquetación.
///
/// <para>
/// El reloj es fijo (<see cref="Hoy"/>, pasado por el parámetro
/// <see cref="Calendario.Hoy"/>): el resultado no depende del día en que se
/// ejecuta la suite ni cambia si cruza medianoche a fin de mes. Marzo de 2026
/// empieza en domingo, así que la primera semana lleva seis días de relleno.
/// </para>
/// </summary>
public class CalendarioTests : BunitContext
{
    public CalendarioTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly DateOnly Hoy = new(2026, 3, 15);
    private static readonly DateOnly PrimeroDelMes = new(Hoy.Year, Hoy.Month, 1);
    private static readonly DateOnly Dia10 = PrimeroDelMes.AddDays(9);
    private static readonly DateOnly Dia11 = PrimeroDelMes.AddDays(10);
    private static readonly DateOnly Dia12 = PrimeroDelMes.AddDays(11);

    private sealed class MediatorPorTipo : IMediator
    {
        public required IReadOnlyList<VencimientoCalendarioDto> Vencimientos { get; init; }
        public required IReadOnlyList<VisitaCalendarioDto> Visitas { get; init; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(request switch
            {
                ObtenerVencimientosMesQuery => (object)Vencimientos,
                ObtenerVisitasParaCalendarioQuery => Visitas,
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            }));

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private IRenderedComponent<Calendario> Renderizar(
        IReadOnlyList<VencimientoCalendarioDto>? vencimientos = null,
        IReadOnlyList<VisitaCalendarioDto>? visitas = null)
    {
        Services.AddScoped<IMediator>(_ => new MediatorPorTipo
        {
            Vencimientos = vencimientos ?? [],
            Visitas = visitas ?? []
        });
        return Render<Calendario>(p => p.Add(c => c.Hoy, Hoy));
    }

    /// <summary>
    /// Mediador cuyas respuestas retiene una compuerta por mes: nada vuelve
    /// hasta que el test la abre, así que el test decide en qué orden
    /// terminan dos cargas solapadas. Anota cada consulta con su mes, y cada
    /// respuesta lleva un vencimiento el día 10 del mes pedido, de modo que la
    /// pantalla delata de qué mes son los datos que pinta.
    /// </summary>
    private sealed class MediatorConCompuertas : IMediator
    {
        private readonly Dictionary<(int Anio, int Mes), TaskCompletionSource> _compuertas = [];

        public List<string> Consultas { get; } = [];

        public void Abrir(DateOnly mes) => Compuerta(mes.Year, mes.Month).SetResult();

        /// <summary>Suelta la carga de ese mes con una excepción, como una consulta que falla.</summary>
        public void Fallar(DateOnly mes) =>
            Compuerta(mes.Year, mes.Month).SetException(new InvalidOperationException("Fallo simulado de la consulta."));

        private TaskCompletionSource Compuerta(int anio, int mes)
        {
            if (!_compuertas.TryGetValue((anio, mes), out var compuerta))
            {
                compuerta = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _compuertas[(anio, mes)] = compuerta;
            }
            return compuerta;
        }

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            var (tipo, anio, mes) = request switch
            {
                ObtenerVencimientosMesQuery q => ("vencimientos", q.Anio, q.Mes),
                ObtenerVisitasParaCalendarioQuery q => ("visitas", q.Anio, q.Mes),
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            };
            Consultas.Add($"{tipo} {anio}-{mes:00}");

            await Compuerta(anio, mes).Task;

            object respuesta = tipo == "vencimientos"
                ? new List<VencimientoCalendarioDto> { Vencimiento(new DateOnly(anio, mes, 10), EstadoDocumento.Vigente) }
                : new List<VisitaCalendarioDto>();
            return (TResponse)respuesta;
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    /// <summary>
    /// Arranca en el mes de <see cref="Hoy"/> con su carga retenida y pulsa
    /// «Mes siguiente» sin esperar: quedan dos cargas en vuelo, la del mes
    /// inicial (obsoleta) y la del siguiente (vigente).
    /// </summary>
    private (IRenderedComponent<Calendario> Cut, MediatorConCompuertas Mediador, Task Clic) DosCargasSolapadas()
    {
        var mediador = new MediatorConCompuertas();
        Services.AddScoped<IMediator>(_ => mediador);
        var cut = Render<Calendario>(p => p.Add(c => c.Hoy, Hoy));

        var clic = cut.Find("button[aria-label='Mes siguiente']").ClickAsync(new());
        return (cut, mediador, clic);
    }

    private static readonly DateOnly MesSiguiente = PrimeroDelMes.AddMonths(1);

    /// <summary>
    /// Tope para esperar el clic: si la pantalla pide un mes cuya compuerta el
    /// test nunca abre (p. ej. porque ignora el reloj recibido), la espera
    /// fallaría colgada en vez de en rojo.
    /// </summary>
    private static readonly TimeSpan EsperaMaxima = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Pulsar «Mes siguiente» mientras aún carga el mes en curso deja dos
    /// cargas abiertas. Si la vieja vuelve la última, no puede pisar a la
    /// nueva: la pantalla dice abril y pinta los datos de abril.
    /// </summary>
    [Fact]
    public async Task Si_la_carga_de_un_mes_ya_abandonado_vuelve_la_ultima_se_descarta()
    {
        var (cut, mediador, clic) = DosCargasSolapadas();

        mediador.Abrir(MesSiguiente);
        await clic.WaitAsync(EsperaMaxima);
        var rendersAntes = cut.RenderCount;
        mediador.Abrir(PrimeroDelMes);

        // La carga obsoleta tiene que haber TERMINADO antes de mirar: si no,
        // el test pasaría solo porque aún no ha vuelto. Es la de
        // OnInitializedAsync, y ComponentBase vuelve a pintar al acabarla.
        cut.WaitForState(() => cut.RenderCount > rendersAntes);

        cut.Find(".calendario-titulo-mes").TextContent.Should().Be("Abril 2026");
        cut.FindAll("button.calendario-celda").Select(b => b.GetAttribute("aria-label"))
            .Should().ContainSingle("abril trae un vencimiento el día 10; si falta, los datos de marzo, que llegaron tarde, han pisado a los de abril")
            .Which.Should().StartWith("10 de abril:", "los datos pintados tienen que ser los del mes que dice el título");
    }

    /// <summary>
    /// Si la carga vieja vuelve PRIMERO, la nueva sigue en vuelo: la pantalla
    /// tiene que seguir cargando, no enseñar los datos de marzo bajo el título
    /// de abril.
    /// </summary>
    [Fact]
    public async Task Si_la_carga_abandonada_vuelve_primero_la_pantalla_sigue_cargando_el_mes_vigente()
    {
        var (cut, mediador, clic) = DosCargasSolapadas();

        var rendersAntes = cut.RenderCount;
        mediador.Abrir(PrimeroDelMes);
        cut.WaitForState(() => cut.RenderCount > rendersAntes);

        cut.Find(".calendario-titulo-mes").TextContent.Should().Be("Abril 2026");
        cut.FindAll(".calendario-rejilla").Should().BeEmpty("abril aún no ha vuelto y lo que ha vuelto es marzo");

        mediador.Abrir(MesSiguiente);
        await clic.WaitAsync(EsperaMaxima);
        cut.WaitForAssertion(() => cut.Find("button.calendario-celda").GetAttribute("aria-label").Should().StartWith("10 de abril:"));
    }

    /// <summary>
    /// Un fallo es una respuesta más: si la carga que falla es la de un mes ya
    /// abandonado, no puede tapar con «No pudimos cargar el calendario» el mes
    /// que sí se cargó bien.
    /// </summary>
    [Fact]
    public async Task Si_falla_la_carga_de_un_mes_ya_abandonado_no_tapa_el_mes_vigente_con_el_error()
    {
        var (cut, mediador, clic) = DosCargasSolapadas();

        mediador.Abrir(MesSiguiente);
        await clic.WaitAsync(EsperaMaxima);
        var rendersAntes = cut.RenderCount;
        mediador.Fallar(PrimeroDelMes);
        cut.WaitForState(() => cut.RenderCount > rendersAntes);

        cut.Markup.Should().NotContain("No pudimos cargar el calendario", "lo que falló es marzo, y la pantalla ya enseña abril");
        cut.Find(".calendario-titulo-mes").TextContent.Should().Be("Abril 2026");
        cut.Find("button.calendario-celda").GetAttribute("aria-label").Should().StartWith("10 de abril:");
    }

    /// <summary>
    /// Las dos consultas de una carga —vencimientos y visitas— piden el mismo
    /// mes aunque el usuario navegue entre una y otra: el mes se fija al
    /// empezar la carga, no se relee después del primer await.
    /// </summary>
    [Fact]
    public async Task Las_dos_consultas_de_una_carga_piden_el_mismo_mes_aunque_se_navegue_entre_ellas()
    {
        var (cut, mediador, clic) = DosCargasSolapadas();

        mediador.Abrir(MesSiguiente);
        await clic.WaitAsync(EsperaMaxima);
        mediador.Abrir(PrimeroDelMes);
        cut.WaitForAssertion(() => mediador.Consultas.Should().HaveCount(4));

        mediador.Consultas.Should().BeEquivalentTo(
            ["vencimientos 2026-03", "visitas 2026-03", "vencimientos 2026-04", "visitas 2026-04"],
            "cada carga pide sus dos consultas del mismo mes; la de marzo no puede pedir las visitas de abril");
    }

    /// <summary>
    /// El reloj que recibe la pantalla manda en las tres cosas que dependen de
    /// «hoy»: el mes con el que arranca, el día resaltado y el mes al que
    /// vuelve el botón «Hoy» tras navegar.
    /// </summary>
    [Fact]
    public async Task La_pantalla_toma_hoy_del_reloj_que_recibe_para_arrancar_resaltar_y_volver()
    {
        var cut = Renderizar();

        cut.Find(".calendario-titulo-mes").TextContent.Should().Be("Marzo 2026");
        cut.FindAll(".calendario-dia-hoy").Select(d => d.TextContent.Trim())
            .Should().Equal(["15"], "el día resaltado es el del reloj recibido, no el del sistema");

        await cut.Find("button[aria-label='Mes siguiente']").ClickAsync(new());
        cut.Find(".calendario-titulo-mes").TextContent.Should().Be("Abril 2026");
        cut.FindAll(".calendario-dia-hoy").Should().BeEmpty("en abril no cae el 15 de marzo");

        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Hoy").ClickAsync(new());
        cut.Find(".calendario-titulo-mes").TextContent.Should().Be("Marzo 2026", "«Hoy» vuelve al mes del reloj recibido");
    }

    private static VencimientoCalendarioDto Vencimiento(DateOnly fecha, EstadoDocumento estado, string trabajador = "Nuria Salas") =>
        new(Guid.NewGuid(), fecha, trabajador, "Certificado de aptitud", estado);

    private static VisitaCalendarioDto Visita(DateOnly inicio, DateOnly fin, bool notificada = false, int trabajadores = 6) =>
        new(Guid.NewGuid(), "Centro Logístico Sur", "Montajes Ebro", inicio, fin, trabajadores, notificada);

    /// <summary>El único botón de día cuyo nombre accesible empieza por ese día del mes.</summary>
    private static AngleSharp.Dom.IElement BotonDelDia(IRenderedComponent<Calendario> cut, DateOnly dia)
    {
        var botones = cut.FindAll("button.calendario-celda")
            .Where(b => b.GetAttribute("aria-label")!.StartsWith($"{dia.Day} de ", StringComparison.Ordinal))
            .ToList();
        botones.Should().ContainSingle($"el día {dia.Day} tiene datos, así que tiene que ser un botón");
        return botones[0];
    }

    /// <summary>
    /// El semáforo es el del sistema (EstadoDocumentoUi): un día cuyo peor
    /// documento es Urgente se pinta en rojo, igual que en Documentos y
    /// Alertas. El mockup lo pintaba en ámbar («solo próximos o urgentes»);
    /// copiarlo daría dos colores distintos al mismo documento según la
    /// pantalla. Y el nombre accesible dice el peor estado con palabras,
    /// porque el color solo no puede ser la información.
    /// </summary>
    [Fact]
    public void Un_dia_cuyo_peor_documento_es_urgente_se_pinta_en_rojo_y_lo_dice_con_palabras()
    {
        var cut = Renderizar(vencimientos:
        [
            Vencimiento(Dia10, EstadoDocumento.Proximo),
            Vencimiento(Dia10, EstadoDocumento.Urgente),
            Vencimiento(Dia11, EstadoDocumento.Proximo)
        ]);

        var dia10 = BotonDelDia(cut, Dia10);
        dia10.QuerySelector(".badge")!.ClassList.Should().Contain("badge-peligro");
        dia10.QuerySelector(".badge")!.TextContent.Trim().Should().Be("2", "el número es el recuento del día, no el de los urgentes");
        dia10.GetAttribute("aria-label").Should().Contain("2 vencimientos (peor estado: urgente)");

        BotonDelDia(cut, Dia11).QuerySelector(".badge")!.ClassList.Should().Contain("badge-advertencia");
    }

    /// <summary>
    /// Solo es botón lo que abre algo: un día sin vencimientos ni visitas no
    /// abre el detalle, así que no puede ser una parada de tabulador muda.
    /// Los días de relleno de otros meses no se consultan (la carga es por
    /// mes) y por eso no pueden afirmar nada: ni botón ni visibles para el
    /// lector de pantalla.
    /// </summary>
    [Fact]
    public void Solo_los_dias_con_datos_son_botones_y_los_de_otro_mes_no_afirman_nada()
    {
        var cut = Renderizar(
            vencimientos: [Vencimiento(Dia10, EstadoDocumento.Vigente)],
            visitas: [Visita(Dia12, Dia12)]);

        cut.FindAll("button.calendario-celda").Should().HaveCount(2);

        var diasDelMes = DateTime.DaysInMonth(PrimeroDelMes.Year, PrimeroDelMes.Month);
        var celdas = cut.FindAll(".calendario-celda");
        celdas.Count.Should().Be(diasDelMes + cut.FindAll(".calendario-celda-fuera").Count);
        (celdas.Count % 7).Should().Be(0, "la rejilla son semanas completas");

        foreach (var fuera in cut.FindAll(".calendario-celda-fuera"))
        {
            fuera.TagName.Should().Be("DIV");
            fuera.GetAttribute("aria-hidden").Should().Be("true");
            fuera.QuerySelector(".badge").Should().BeNull();
        }
    }

    /// <summary>
    /// La primera semana se completa con los últimos días del mes anterior, en
    /// orden: si el mes empieza en jueves, delante van lunes, martes y
    /// miércoles del mes anterior.
    /// </summary>
    [Fact]
    public void La_primera_semana_se_completa_con_los_ultimos_dias_del_mes_anterior_en_orden()
    {
        var cut = Renderizar();

        var huecosDelante = ((int)PrimeroDelMes.DayOfWeek + 6) % 7;
        var esperados = Enumerable.Range(1, huecosDelante)
            .Select(i => PrimeroDelMes.AddDays(i - huecosDelante - 1).Day.ToString())
            .ToList();

        var delante = cut.FindAll(".calendario-celda").Take(huecosDelante)
            .Select(c => c.TextContent.Trim()).ToList();

        delante.Should().Equal(esperados);
        cut.FindAll(".calendario-celda").Take(huecosDelante)
            .Should().OnlyContain(c => c.ClassList.Contains("calendario-celda-fuera"));
    }

    /// <summary>
    /// Una visita de varios días marca cada día de su rango (comportamiento
    /// del código anterior que se conserva) y, cuando coinciden varias, el
    /// día dice cuántas: la barra del mockup sola perdería ese recuento.
    /// </summary>
    [Fact]
    public void Una_visita_de_varios_dias_marca_cada_dia_y_si_coinciden_varias_se_cuentan()
    {
        var cut = Renderizar(visitas: [Visita(Dia10, Dia12), Visita(Dia11, Dia11)]);

        BotonDelDia(cut, Dia10).QuerySelector(".calendario-barra-visita").Should().NotBeNull();
        BotonDelDia(cut, Dia10).QuerySelector(".calendario-visitas-numero").Should().BeNull("una sola visita no lleva número");
        BotonDelDia(cut, Dia12).QuerySelector(".calendario-barra-visita").Should().NotBeNull();

        var dia11 = BotonDelDia(cut, Dia11);
        dia11.QuerySelector(".calendario-visitas-numero").Should().NotBeNull("ese día coinciden dos visitas");
        dia11.QuerySelector(".calendario-visitas-numero")!.TextContent.Trim().Should().Be("2");
        dia11.GetAttribute("aria-label").Should().EndWith("2 visitas programadas");
    }

    /// <summary>
    /// El detalle del día conserva lo que el código anterior mostraba y el
    /// mockup omitía —si la visita está notificada y cuántos trabajadores
    /// lleva— y resume el día arriba, como pide el mockup.
    /// </summary>
    [Fact]
    public async Task El_detalle_del_dia_resume_el_dia_y_conserva_notificacion_y_trabajadores()
    {
        var cut = Renderizar(
            vencimientos: [Vencimiento(Dia10, EstadoDocumento.Vencido, "Juan Pérez")],
            visitas: [Visita(Dia10, Dia11, notificada: false, trabajadores: 1)]);

        await BotonDelDia(cut, Dia10).ClickAsync(new());

        var dialogo = cut.Find("[role=dialog]");
        dialogo.QuerySelector("h2")!.TextContent.Should().Be(Dia10.ToString("d 'de' MMMM 'de' yyyy", new System.Globalization.CultureInfo("es-ES")));
        dialogo.QuerySelector(".calendario-resumen-dia")!.TextContent.Should().EndWith("· 1 vencimiento · 1 visita");
        dialogo.TextContent.Should().Contain("Juan Pérez").And.Contain("Vencido");
        dialogo.TextContent.Should().Contain("Sin notificar", "el estado de notificación viene del código anterior y el mockup lo había perdido");
        dialogo.TextContent.Should().Contain($"Montajes Ebro · {Dia10:dd/MM}–{Dia11:dd/MM} · 1 trabajador");
    }

    /// <summary>«Gestionar» sigue llevando al drawer del documento en Documentos, como antes.</summary>
    [Fact]
    public async Task Gestionar_un_vencimiento_lleva_al_documento_en_documentos()
    {
        var vencimiento = Vencimiento(Dia10, EstadoDocumento.Proximo);
        var cut = Renderizar(vencimientos: [vencimiento]);

        await BotonDelDia(cut, Dia10).ClickAsync(new());
        await cut.FindAll("[role=dialog] button").First(b => b.TextContent.Trim() == "Gestionar").ClickAsync(new());

        Services.GetRequiredService<NavigationManager>().Uri
            .Should().EndWith($"/documentos?documentoId={vencimiento.DocumentoId}");
    }
}
