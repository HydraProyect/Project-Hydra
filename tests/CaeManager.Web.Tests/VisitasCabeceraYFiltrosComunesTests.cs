using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Configuracion.Commands.GuardarVistaRecordada;
using CaeManager.Application.Configuracion.Commands.OlvidarVistaRecordada;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Application.Visitas;
using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Domain.Common;
using CaeManager.Domain.Visitas;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Visitas.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Visitas usa la cabecera y la barra de filtros comunes de los listados (<c>CabeceraListado</c>,
/// <c>BarraFiltros</c> en modo pastillas) y la franja de estado (<c>FranjaEstado</c>) sobre la columna
/// «Documentación». El cambio de piezas no puede perder ningún filtro de los que tenía la barra antigua
/// (búsqueda, «Solo activas», «Solo urgentes», «Notificada…» y el orden), y todos siguen viajando en la URL.
/// </summary>
public class VisitasCabeceraYFiltrosComunesTests : BunitContext
{
    private static readonly DateOnly Hoy = DiaDeNegocio.Hoy();

    public VisitasCabeceraYFiltrosComunesTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        this.ConRolDeEscritura();
    }

    private sealed class Mediador : IMediator
    {
        public List<VisitaListaDto> Visitas { get; } = [];
        public IReadOnlyDictionary<string, int>? Recuentos { get; set; }
        public List<ObtenerVisitasQuery> Consultas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is ObtenerVisitasQuery consulta)
            {
                Consultas.Add(consulta);
                return Task.FromResult((TResponse)(object)new ResultadoPaginado<VisitaListaDto>(
                    Visitas, Visitas.Count, consulta.Pagina, consulta.TamanoPagina)
                { RecuentosPorEstado = Recuentos });
            }

            return Task.FromResult((TResponse)(object)(request switch
            {
                // Las piezas de filtros guardados y de vista recordada que monta la página: nada guardado.
                ObtenerVistaRecordadaQuery => (object)null!,
                ObtenerFiltrosGuardadosQuery => (IReadOnlyList<FiltroGuardadoDto>)[],
                GuardarVistaRecordadaCommand or OlvidarVistaRecordadaCommand => Result.Exito(),
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

    private readonly Mediador _mediador = new();

    private static VisitaListaDto Visita(string centro) =>
        new(Guid.NewGuid(), Guid.NewGuid(), centro, Guid.NewGuid(), "Iberojet S.A.", Guid.NewGuid(), "Instalaciones Arbeko S.L.",
            Hoy, Hoy.AddDays(2), TotalTrabajadores: 3, DocumentacionCompleta: false, NotificadoCliente: false,
            OrigenVisita.Correo, NivelUrgenciaVisita.Normal, Version: Guid.NewGuid());

    private IRenderedComponent<Visitas> Renderizar(string? url = null)
    {
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        if (url is not null)
            Services.GetRequiredService<NavigationManager>().NavigateTo(url);

        return Render<Visitas>();
    }

    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    // ------------------------------------------------------------------ cabecera

    [Fact]
    public void La_cabecera_es_la_comun_con_recuento_atajos_y_seleccion_multiple_y_sin_antetitulo()
    {
        _mediador.Visitas.AddRange([Visita("Planta Zaragoza"), Visita("Sede Sevilla")]);

        var cut = Renderizar();

        cut.Find("h1").TextContent.Trim().Should().Be("Visitas");
        cut.Find(".cabecera-listado-contador").TextContent.Trim().Should().Be("2");
        cut.FindAll("[data-abrir-atajos]").Should().ContainSingle("el botón de atajos (KeyTip K) lo pone CabeceraListado")
            .Which.GetAttribute("data-keytip").Should().Be("K");
        cut.Find("button[aria-label='Selección múltiple']").GetAttribute("data-keytip").Should().Be("S");
        cut.Find("[data-keytip-contenedor='N']").TextContent.Should().Contain("Nueva visita");
        cut.Markup.Should().NotContain("Operación", "la cabecera de listado no lleva antetítulo: el grupo ya está en las migas");
    }

    // ------------------------------------------------------------------- filtros

    /// <summary>Inventario: los cinco controles de la barra antigua siguen estando, uno por uno.</summary>
    [Fact]
    public void La_barra_conserva_todos_los_filtros_que_tenia_la_antigua()
    {
        var cut = Renderizar();

        cut.Find(".barra-filtros-pastillas [data-filtro-pantalla]").GetAttribute("placeholder")
            .Should().Be("Buscar por centro, titular o empresa…");
        cut.FindAll(".barra-filtros-pastillas .menu-acciones-disparador-pastilla").Select(p => p.GetAttribute("aria-label"))
            .Should().Equal(
                ["Solo activas: Sí", "Solo urgentes", "Notificada al titular del Centro", "Más filtros"],
                "«Solo activas» nace marcado; el orden vive en «Más filtros»");

        OpcionesDe(cut, "Solo activas").Should().Equal("No", "Sí");
        OpcionesDe(cut, "Solo urgentes").Should().Equal("No", "Sí");
        OpcionesDe(cut, "Notificada al titular del Centro").Should().Equal("Todas", "Sí", "No");
        OpcionesDe(cut, "Más filtros").Should().Equal("Por fechas", "Documentación por gestionar primero");
    }

    private static IEnumerable<string> OpcionesDe(IRenderedComponent<Visitas> cut, string pastilla)
    {
        if (FiltrosVisitasDePrueba.Pastilla(cut, pastilla).GetAttribute("aria-expanded") != "true")
            FiltrosVisitasDePrueba.Pastilla(cut, pastilla).Click();
        return cut.Find("#" + FiltrosVisitasDePrueba.Pastilla(cut, pastilla).GetAttribute("aria-controls"))
            .QuerySelectorAll("[role=menuitemradio]").Select(o => o.TextContent.Trim()).ToList();
    }

    [Fact]
    public void Cada_filtro_viaja_en_la_url_y_llega_a_la_consulta()
    {
        var cut = Renderizar();

        FiltrosVisitasDePrueba.Elegir(cut, "Solo activas", "No");
        FiltrosVisitasDePrueba.Elegir(cut, "Solo urgentes", "Sí");
        FiltrosVisitasDePrueba.Elegir(cut, "Notificada al titular del Centro", "No");
        FiltrosVisitasDePrueba.Elegir(cut, "Más filtros", "Documentación por gestionar primero");

        Navegacion.Uri.Should().Contain("activas=false").And.Contain("urgentes=true")
            .And.Contain("notificado=no").And.Contain("orden=documentacion");
        var consulta = _mediador.Consultas.Last();
        consulta.SoloActivas.Should().BeFalse();
        consulta.SoloUrgentes.Should().BeTrue();
        consulta.NotificadoCliente.Should().BeFalse();
        consulta.OrdenarPor.Should().Be(nameof(VisitaListaDto.PorGestionar));
    }

    [Fact]
    public void Los_filtros_del_usuario_salen_como_chips_y_el_de_fabrica_y_el_orden_no()
    {
        var cut = Renderizar("visitas?q=Zaragoza&urgentes=true&notificado=si&orden=documentacion");

        cut.FindAll(".chips-filtros .chip-filtro").Select(c => c.TextContent.Trim())
            .Should().Equal("Búsqueda: Zaragoza", "Solo urgentes", "Notificada al titular del Centro: Sí");
    }

    // -------------------------------------------------------------------- franja

    [Fact]
    public void La_franja_enseña_los_cuatro_estados_de_la_documentacion_con_su_recuento()
    {
        _mediador.Visitas.Add(Visita("Planta Zaragoza"));
        _mediador.Recuentos = new Dictionary<string, int>
        {
            [nameof(EstadoDocumentacionVisita.PorGestionar)] = 3,
            [nameof(EstadoDocumentacionVisita.Gestionada)] = 2,
            [nameof(EstadoDocumentacionVisita.SinGestionCae)] = 1,
            [nameof(EstadoDocumentacionVisita.Cancelada)] = 0,
        };

        var cut = Renderizar();

        _mediador.Consultas.Last().ConRecuentosPorEstado.Should().BeTrue();
        cut.FindAll(".franja-estado button[data-estado]")
            .Select(b => (b.GetAttribute("data-estado"), b.QuerySelector(".franja-estado-recuento")!.TextContent + " " + b.LastChild!.TextContent.Trim()))
            .Should().Equal(
                ("PorGestionar", "3 Por gestionar"),
                ("Gestionada", "2 Gestionada"),
                ("SinGestionCae", "1 No requiere gestión CAE"),
                ("Cancelada", "0 Cancelada"));
        cut.Find(".franja-estado").GetAttribute("aria-label").Should().Be("Documentación");
    }

    [Fact]
    public void Marcar_estados_en_la_franja_los_escribe_en_la_url_y_filtra_la_consulta()
    {
        _mediador.Visitas.Add(Visita("Planta Zaragoza"));
        var cut = Renderizar();
        _mediador.Consultas.Last().EstadosDocumentacion.Should().BeEmpty("sin selección no se filtra por estado");

        cut.Find(".franja-estado button[data-estado=PorGestionar]").Click();
        cut.Find(".franja-estado button[data-estado=Cancelada]").Click();

        Navegacion.Uri.Should().Contain("estado=PorGestionar%2CCancelada");
        _mediador.Consultas.Last().EstadosDocumentacion.Should()
            .Equal(EstadoDocumentacionVisita.PorGestionar, EstadoDocumentacionVisita.Cancelada);
        cut.Find(".franja-estado button[data-estado=PorGestionar]").GetAttribute("aria-pressed").Should().Be("true");
    }

    [Fact]
    public void Un_enlace_con_estado_lo_aplica_y_descarta_lo_que_no_es_un_estado()
    {
        Renderizar("visitas?estado=Gestionada,Inventado,7");

        _mediador.Consultas.Last().EstadosDocumentacion.Should().Equal(EstadoDocumentacionVisita.Gestionada);
    }

    [Fact]
    public void El_estado_de_la_franja_cuenta_como_filtro_y_quitar_los_filtros_lo_borra_de_la_url()
    {
        var cut = Renderizar("visitas?estado=Gestionada");

        cut.Markup.Should().Contain("Ninguna visita con estos filtros");
        cut.Find(".estado-vacio button").Click();

        Navegacion.Uri.Should().NotContain("estado", "dejarlo en la URL lo devuelve en la siguiente pasada de parámetros");
        _mediador.Consultas.Last().EstadosDocumentacion.Should().BeEmpty();
    }
}
