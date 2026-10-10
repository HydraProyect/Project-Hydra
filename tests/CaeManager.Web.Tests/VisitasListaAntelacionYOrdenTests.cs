using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Domain.Common;
using CaeManager.Domain.Visitas;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Visitas;
using CaeManager.Web.Features.Visitas.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Lista de Visitas: antelación y plazo en la fila (mismo vocabulario que el detalle) y
/// orden «documentación por gestionar primero» por la URL (<c>?orden=documentacion</c>).
/// </summary>
public class VisitasListaAntelacionYOrdenTests : BunitContext
{
    private static readonly DateOnly Hoy = DiaDeNegocio.Hoy();

    public VisitasListaAntelacionYOrdenTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        this.ConRolDeEscritura();
    }

    // ---- Plazo: frontera de medianoche en Madrid -------------------------------------------

    [Theory]
    [InlineData("2026-10-03T22:30:00Z", "2026-10-04", PlazoVisita.Hoy)]     // 00:30 del 4-oct en Madrid (UTC+2)
    [InlineData("2026-10-03T21:59:59Z", "2026-10-04", PlazoVisita.Manana)]  // 23:59:59 del 3-oct en Madrid
    [InlineData("2026-12-31T23:30:00Z", "2027-01-01", PlazoVisita.Hoy)]     // 00:30 del 1-ene en Madrid (UTC+1)
    [InlineData("2026-12-31T22:59:59Z", "2027-01-01", PlazoVisita.Manana)]  // 23:59:59 del 31-dic en Madrid
    public void El_plazo_se_cuenta_en_dia_de_negocio_de_Madrid_no_en_fecha_UTC(string instanteUtc, string fechaVisita, PlazoVisita esperado)
    {
        var visita = DateOnly.Parse(fechaVisita);

        AntelacionVisitaUi.Plazo(visita, visita, DiaDeNegocio.De(DateTimeOffset.Parse(instanteUtc))).Plazo.Should().Be(esperado);
    }

    [Fact]
    public void Control_positivo_la_fecha_UTC_daria_otro_plazo_en_la_frontera()
    {
        // Sin este control la frontera de arriba no demostraría nada: la fecha UTC de ese
        // mismo instante es el 3, y la visita del 4 saldría «mañana» en vez de «hoy».
        var instante = DateTimeOffset.Parse("2026-10-03T22:30:00Z");
        var visita = new DateOnly(2026, 10, 4);

        AntelacionVisitaUi.Plazo(visita, visita, DateOnly.FromDateTime(instante.UtcDateTime)).Plazo.Should().Be(PlazoVisita.Manana);
        AntelacionVisitaUi.Plazo(visita, visita, DiaDeNegocio.De(instante)).Plazo.Should().Be(PlazoVisita.Hoy);
    }

    [Theory]
    [InlineData(-1, 3, PlazoVisita.EnCurso, 0)]
    [InlineData(0, 0, PlazoVisita.Hoy, 0)]
    [InlineData(1, 1, PlazoVisita.Manana, 1)]
    [InlineData(5, 6, PlazoVisita.EnDias, 5)]
    [InlineData(-4, -1, PlazoVisita.Finalizada, 0)]
    public void El_plazo_distingue_los_cinco_casos(int desdeHoy, int hastaHoy, PlazoVisita plazo, int dias)
    {
        var hoy = new DateOnly(2026, 10, 4);

        AntelacionVisitaUi.Plazo(hoy.AddDays(desdeHoy), hoy.AddDays(hastaHoy), hoy).Should().Be((plazo, dias));
    }

    // ---- Fila y orden en la pantalla -------------------------------------------------------

    private sealed class Mediador : IMediator
    {
        public List<VisitaListaDto> Visitas { get; } = [];
        public List<ObtenerVisitasQuery> Consultas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is not ObtenerVisitasQuery q)
                throw new NotSupportedException(request.GetType().Name);
            Consultas.Add(q);
            return Task.FromResult((TResponse)(object)new ResultadoPaginado<VisitaListaDto>(Visitas, Visitas.Count, q.Pagina, q.TamanoPagina));
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    private static VisitaListaDto Visita(string centro, int diasHastaInicio, TramoAntelacion? tramo = null, bool cancelada = false) =>
        new(Guid.NewGuid(), Guid.NewGuid(), centro, Guid.NewGuid(), "Iberojet S.A.", Guid.NewGuid(), "Arbeko S.L.",
            Hoy.AddDays(diasHastaInicio), Hoy.AddDays(diasHastaInicio), 2, DocumentacionCompleta: false, false,
            OrigenVisita.Correo, NivelUrgenciaVisita.Normal, EstaCancelada: cancelada,
            Tramo: tramo, AntelacionNominalHoras: tramo is null ? null : 71.5m, AntelacionEfectivaHoras: tramo is null ? null : 15m);

    private IRenderedComponent<Visitas> Renderizar(Mediador mediador, string? orden = null)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        if (orden is not null)
            Services.GetRequiredService<NavigationManager>().NavigateTo($"/visitas?orden={orden}");
        return Render<Visitas>();
    }

    [Fact]
    public void La_fila_muestra_el_tramo_con_el_mismo_texto_y_tono_que_el_detalle_y_nada_si_no_hay()
    {
        var mediador = new Mediador();
        mediador.Visitas.AddRange([Visita("Con tramo", 1, TramoAntelacion.Expres), Visita("Sin tramo", 1)]);

        var cut = Renderizar(mediador);

        var filas = cut.FindAll("tbody tr");
        var span = filas.Single(f => f.TextContent.Contains("Con tramo")).QuerySelector("[data-antelacion-tramo]")!;
        span.TextContent.Trim().Should().Be(AntelacionVisitaUi.Texto(TramoAntelacion.Expres));
        var badgeEsperado = Render<Badge>(p => p.Add(b => b.Tono, AntelacionVisitaUi.Tono(TramoAntelacion.Expres)));
        span.QuerySelector("span")!.ClassName.Should().Be(badgeEsperado.Find("span").ClassName);
        span.GetAttribute("title").Should().Contain("71,5 h").And.Contain("15 h");
        filas.Single(f => f.TextContent.Contains("Sin tramo")).QuerySelector("[data-antelacion-tramo]").Should().BeNull();
    }

    [Fact]
    public void La_fila_dice_hoy_y_manana_y_no_pinta_plazo_en_una_cancelada()
    {
        var mediador = new Mediador();
        mediador.Visitas.AddRange([Visita("Entra hoy", 0), Visita("Entra manana", 1), Visita("Entra en cinco", 5), Visita("Anulada", 2, cancelada: true)]);

        var cut = Renderizar(mediador);

        string Plazo(string centro) => cut.FindAll("tbody tr").Single(f => f.TextContent.Contains(centro))
            .QuerySelector("span.visitas-celda-secundaria")?.TextContent.Trim() ?? "";
        Plazo("Entra hoy").Should().Be("Hoy");
        Plazo("Entra manana").Should().Be("Mañana");
        Plazo("Entra en cinco").Should().Be("En 5 días");
        Plazo("Anulada").Should().BeEmpty();
    }

    [Fact]
    public void Por_defecto_la_consulta_no_pide_el_orden_por_gestionar()
    {
        var mediador = new Mediador();
        mediador.Visitas.Add(Visita("A", 1));

        Renderizar(mediador);

        mediador.Consultas.Should().NotBeEmpty().And.OnlyContain(q => q.OrdenarPor != nameof(VisitaListaDto.PorGestionar));
    }

    [Fact]
    public void El_parametro_orden_documentacion_de_la_url_pide_por_gestionar_descendente()
    {
        var mediador = new Mediador();
        mediador.Visitas.Add(Visita("A", 1));

        Renderizar(mediador, orden: "documentacion");

        mediador.Consultas.Should().Contain(q => q.OrdenarPor == nameof(VisitaListaDto.PorGestionar) && q.Descendente);
    }

    [Fact]
    public async Task Elegir_el_orden_en_el_selector_lo_escribe_en_la_url_y_vuelve_a_pedir_la_lista()
    {
        var mediador = new Mediador();
        mediador.Visitas.Add(Visita("A", 1));
        var cut = Renderizar(mediador);
        var navegacion = Services.GetRequiredService<NavigationManager>();

        await FiltrosVisitasDePrueba.ElegirAsync(cut, "Más filtros", "Documentación por gestionar primero");

        navegacion.Uri.Should().Contain("orden=documentacion");
        mediador.Consultas.Last().OrdenarPor.Should().Be(nameof(VisitaListaDto.PorGestionar));
    }
}
