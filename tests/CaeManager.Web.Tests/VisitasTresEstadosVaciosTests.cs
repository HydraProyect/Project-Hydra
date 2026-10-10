using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Visitas.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Visitas necesita <b>TRES</b> estados vacíos, no los dos del resto de listas, y
/// la razón es que «Solo activas» viene <b>marcado de fábrica</b>
/// (<c>_soloActivas = true</c>).
///
/// <para>
/// El patrón de dos estados no se traslada aquí:
/// <list type="bullet">
/// <item>contar ese filtro como puesto por el usuario dejaría «Todavía no hay
/// visitas» inalcanzable, porque siempre habría un filtro activo;</item>
/// <item>no contarlo haría que esa misma frase mintiera a quien sí tiene
/// visitas, pero todas finalizadas.</item>
/// </list>
/// Cada uno de los tres dice algo cierto y ofrece la salida que corresponde.
/// </para>
/// </summary>
public class VisitasTresEstadosVaciosTests : BunitContext
{
    public VisitasTresEstadosVaciosTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        // Los textos de Visitas salen de IStringLocalizer<TextosVisitas>.
        Services.AddLocalization();
        this.ConRolDeEscritura();
    }

    private sealed class MediatorPorTipo : IMediator
    {
        public required IReadOnlyList<VisitaListaDto> Visitas { get; init; }

        /// <summary>Lo que la pantalla pidió, para poder afirmar QUÉ filtro viajó.</summary>
        public ObtenerVisitasQuery? UltimaConsulta { get; private set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is ObtenerVisitasQuery consulta)
            {
                UltimaConsulta = consulta;
                return Task.FromResult((TResponse)(object)new ResultadoPaginado<VisitaListaDto>(
                    Visitas, Visitas.Count, consulta.Pagina, consulta.TamanoPagina));
            }

            return Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerCentrosParaSelectorQuery => Array.Empty<CentroSelectorDto>(),
                ObtenerTrabajadoresParaSelectorQuery => (object)Array.Empty<TrabajadorSelectorDto>(),
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            }));
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

    private MediatorPorTipo _mediator = null!;

    /// <param name="notificado">Filtro que llega por la URL (?notificado=).</param>
    /// <summary>Dirección completa de partida, para los casos que no caben en <c>notificado</c>. Se fija antes de renderizar.</summary>
    private string? _url;

    // ------------------- «Solo activas» y «Solo urgentes» viajan en la URL (T20)

    [Fact]
    public void Un_enlace_con_los_dos_conmutadores_los_lleva_a_la_consulta()
    {
        _url = "visitas?activas=false&urgentes=true";

        Renderizar();

        _mediator.UltimaConsulta!.SoloActivas.Should().BeFalse();
        _mediator.UltimaConsulta!.SoloUrgentes.Should().BeTrue();
    }

    [Fact]
    public void Marcar_solo_urgentes_lo_escribe_en_la_url_y_quitar_los_filtros_lo_borra()
    {
        var cut = Renderizar();
        var navegacion = Services.GetRequiredService<NavigationManager>();

        FiltrosVisitasDePrueba.Elegir(cut, "Solo urgentes", "Sí");

        navegacion.Uri.Should().Contain("urgentes=true");
        _mediator.UltimaConsulta!.SoloUrgentes.Should().BeTrue();

        cut.Find(".estado-vacio button").Click();

        navegacion.Uri.Should().NotContain("urgentes", "dejarlo en la URL lo devuelve en la siguiente pasada de parámetros");
        _mediator.UltimaConsulta!.SoloUrgentes.Should().BeFalse();
    }

    /// <summary>
    /// «Solo activas» nace marcado: la dirección sin parámetros es la vista de
    /// fábrica, y solo desmarcarlo deja rastro. Sin ese rastro, la siguiente
    /// pasada de parámetros lo volvería a marcar.
    /// </summary>
    [Fact]
    public void Ver_tambien_las_finalizadas_lo_escribe_en_la_url_y_volver_a_marcarlo_lo_quita()
    {
        var cut = Renderizar();
        var navegacion = Services.GetRequiredService<NavigationManager>();
        navegacion.Uri.Should().NotContain("activas");

        cut.FindAll("button").First(b => b.TextContent.Contains("Ver también las finalizadas")).Click();

        navegacion.Uri.Should().Contain("activas=false");
        _mediator.UltimaConsulta!.SoloActivas.Should().BeFalse();

        FiltrosVisitasDePrueba.Elegir(cut, "Solo activas", "Sí");

        navegacion.Uri.Should().NotContain("activas");
        _mediator.UltimaConsulta!.SoloActivas.Should().BeTrue();
    }

    private IRenderedComponent<Visitas> Renderizar(string? notificado = null, params VisitaListaDto[] visitas)
    {
        _mediator = new MediatorPorTipo { Visitas = visitas };
        Services.AddScoped<IMediator>(_ => _mediator);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();

        if (_url is not null)
            Services.GetRequiredService<NavigationManager>().NavigateTo(_url);
        else if (notificado is not null)
            Services.GetRequiredService<NavigationManager>()
                .NavigateTo("visitas?notificado=" + Uri.EscapeDataString(notificado));

        return Render<Visitas>();
    }

    /// <summary>
    /// El estado de partida: nadie ha tocado nada y «Solo activas» está marcado
    /// de fábrica. Decir «crea la primera» aquí sería falso si hay visitas
    /// finalizadas detrás, y ofrecer «quitar los filtros» señalaría algo que el
    /// usuario no puso.
    /// </summary>
    [Fact]
    public void Sin_resultados_y_solo_con_el_filtro_de_fabrica_no_promete_que_no_haya_ninguna()
    {
        var cut = Renderizar();

        _mediator.UltimaConsulta!.SoloActivas.Should().BeTrue(
            "el filtro de fábrica tiene que haber viajado en la consulta; si no, este test mediría otra cosa");

        cut.Markup.Should().Contain("No hay visitas activas");
        cut.Markup.Should().Contain("Ver también las finalizadas");
        cut.Markup.Should().NotContain("Todavía no hay visitas programadas",
            "puede haber visitas finalizadas detrás: afirmar que no hay ninguna sería mentir");
        cut.Markup.Should().NotContain("Ninguna visita con estos filtros",
            "no hay ningún filtro puesto por el usuario al que culpar");
    }

    /// <summary>
    /// D-30: «+ Nueva visita» de la cabecera es la primaria; la del estado vacío (en los dos
    /// vacíos que la ofrecen) baja a secundaria para que no haya dos primarias a la vez.
    /// </summary>
    [Fact]
    public void En_los_vacios_la_nueva_visita_de_la_cabecera_es_la_unica_primaria()
    {
        var cut = Renderizar();

        // Vacío «sin activas»: «Ver también las finalizadas» y «+ Nueva visita» juntas.
        cut.FindAll("button.boton-primario").Select(b => b.TextContent.Trim())
            .Should().Equal("+ Nueva visita");
        cut.Find(".estado-vacio").QuerySelectorAll("button").Single(b => b.TextContent.Contains("Nueva visita"))
            .ClassList.Should().Contain("boton-secundario");

        // Vacío inicial (sin ninguna visita, tampoco finalizadas).
        cut.FindAll("button").First(b => b.TextContent.Contains("Ver también las finalizadas")).Click();
        cut.Markup.Should().Contain("Todavía no hay visitas programadas");
        cut.FindAll("button.boton-primario").Select(b => b.TextContent.Trim())
            .Should().Equal("+ Nueva visita");
    }

    [Fact]
    public void Al_ver_tambien_las_finalizadas_y_seguir_vacio_si_dice_que_no_hay_ninguna()
    {
        var cut = Renderizar();

        cut.FindAll("button").First(b => b.TextContent.Contains("Ver también las finalizadas")).Click();

        _mediator.UltimaConsulta!.SoloActivas.Should().BeFalse(
            "el botón tiene que haber desmarcado el filtro de fábrica de verdad, no solo cambiar el texto");
        cut.Markup.Should().Contain("Todavía no hay visitas programadas");
        cut.Markup.Should().NotContain("No hay visitas activas");
    }

    [Fact]
    public void Con_un_filtro_del_usuario_culpa_al_filtro_y_ofrece_quitarlo()
    {
        var cut = Renderizar();

        // "Solo urgentes" sí lo pone el usuario.
        FiltrosVisitasDePrueba.Elegir(cut, "Solo urgentes", "Sí");

        cut.Markup.Should().Contain("Ninguna visita con estos filtros");
        cut.Markup.Should().Contain("Quitar los filtros");
        cut.Markup.Should().NotContain("Todavía no hay visitas programadas");
    }

    /// <summary>
    /// «Quitar los filtros» tiene que borrar de la URL <b>todos</b> los filtros
    /// que viajan por ella, no solo <c>q</c>. <c>notificado</c> se quedaba
    /// puesto y <c>OnParametersSet</c> —que re-sincroniza desde la URL— lo
    /// devolvía en la siguiente pasada de parámetros: la lista seguía igual de
    /// recortada después de pulsar.
    ///
    /// <para>
    /// Encontrado el 2026-09-08 al barrer las pantallas hermanas de Clientes y
    /// Documentos, que tenían el mismo defecto. Las tres pasaban el trinquete
    /// de fuente, que solo comprueba que la rama exista.
    /// </para>
    /// </summary>
    [Fact]
    public void Quitar_los_filtros_borra_tambien_el_de_notificado_de_la_url()
    {
        // «si»: un valor que la página reconoce. Con uno que no (antes se probaba con «pendiente») la página lo
        // descarta como cualquier valor inválido de la URL y no hay filtro del usuario que quitar.
        var cut = Renderizar(notificado: "si");
        cut.Markup.Should().Contain("Ninguna visita con estos filtros", "es el punto de partida de este caso");

        cut.Find(".estado-vacio button").Click();

        var uri = Services.GetRequiredService<NavigationManager>().Uri;
        uri.Should().NotContain("notificado", "dejarlo en la URL lo devuelve en la siguiente pasada de parámetros");

        cut.Markup.Should().NotContain("Ninguna visita con estos filtros");
        cut.Markup.Should().Contain("No hay visitas activas",
            "sin filtros del usuario vuelve el estado de partida, con «Solo activas» de fábrica");
    }

    /// <summary>
    /// Desmarcar «Solo activas» ENSANCHA la lista, así que nunca puede ser la
    /// causa de que no salga nada: no cuenta como filtro del usuario.
    /// </summary>
    [Fact]
    public void Desmarcar_solo_activas_no_cuenta_como_filtrar()
    {
        var cut = Renderizar();

        cut.FindAll("button").First(b => b.TextContent.Contains("Ver también las finalizadas")).Click();

        cut.Markup.Should().NotContain("Ninguna visita con estos filtros",
            "ensanchar la lista no es filtrarla");
    }
}
