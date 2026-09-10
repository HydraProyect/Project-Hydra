using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Proyectos.Commands.CerrarProyecto;
using CaeManager.Application.Proyectos.Commands.DesasignarTecnicoProyecto;
using CaeManager.Application.Proyectos.Queries.ObtenerProyectoPorId;
using CaeManager.Application.Proyectos.Queries.ObtenerProyectos;
using CaeManager.Application.Proyectos.Queries.ObtenerTecnicosProyecto;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Proyectos.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Pantalla de Proyectos en Gen 2 (Proyectos TALVEG.dc.html): lista maestro-
/// detalle colgada de un cliente, filtros de estado y búsqueda en la URL, y el
/// detalle en un panel lateral en vez de una tarjeta bajo la tabla.
///
/// <para>
/// <b>Lo que esto SÍ observa:</b> los seis estados de la lista (sin cliente,
/// cargando no, error, vacía, vacía por filtro, con datos), que el filtrado se
/// hace sobre la lista completa del cliente, que «Quitar los filtros» limpia
/// la URL, qué peticiones llegan al mediador, y que el panel conserva las
/// acciones que el código ya tenía y el mockup no dibuja (dar de baja a un
/// técnico).
/// </para>
///
/// <para>
/// <b>Lo que NO observa:</b> el aspecto (el CSS aislado no se aplica en bUnit),
/// la autorización de los comandos —vive en sus handlers y en
/// <c>AutorizacionEscrituraBehavior</c>, probados en Application—, el alcance
/// de cartera de <c>ObtenerProyectosQuery</c>, ni la pestaña Documentos, que
/// delega en <c>PestanaDocumentacion</c>.
/// </para>
/// </summary>
public class ProyectosGen2Tests : BunitContext
{
    /// <summary>Drawer y Modal importan dialogo-foco.js al abrirse; queda fuera de lo que se observa aquí.</summary>
    public ProyectosGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly Guid ClienteId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid AbiertoId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CerradoId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TecnicoActivoId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid TecnicoDeBajaId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static readonly ProyectoListaDto ProyectoAbierto = new(
        AbiertoId, "Ampliación línea de frío — nave 3", Guid.NewGuid(), "Centro Logístico Norte",
        new DateOnly(2026, 6, 2), new DateOnly(2026, 9, 30), null, EstaAbierto: true);

    private static readonly ProyectoListaDto ProyectoCerrado = new(
        CerradoId, "Sustitución de red contra incendios", Guid.NewGuid(), "Almacén Portugalete",
        new DateOnly(2026, 1, 9), null, new DateOnly(2026, 3, 4), EstaAbierto: false);

    private static ProyectoDetalleDto DetalleDe(ProyectoListaDto p) => new(
        p.Id, ClienteId, "Refrielectric S.L.", p.CentroId, p.CentroNombre, p.Nombre,
        p.FechaInicio, p.FechaFinPrevista, p.FechaCierreReal, p.EstaAbierto,
        Notas: null, TecnicosActivos: 1, DocumentosGestionados: 3, Version: Guid.NewGuid());

    /// <summary>Mediador que responde por tipo y registra todo lo que se le envía.</summary>
    private sealed class MediatorProyectos : IMediator
    {
        public List<ProyectoListaDto> Proyectos { get; set; } = [];
        public bool FallarAlCargarProyectos { get; set; }
        public List<object> Enviados { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);

            object? respuesta = request switch
            {
                ObtenerClientesParaSelectorQuery => new[] { new ClienteSelectorDto(ClienteId, "Refrielectric S.L.") },
                ObtenerCentrosParaSelectorQuery => Array.Empty<CentroSelectorDto>(),
                ObtenerProyectosQuery when FallarAlCargarProyectos => throw new InvalidOperationException("fallo simulado de la consulta"),
                ObtenerProyectosQuery => (IReadOnlyList<ProyectoListaDto>)Proyectos.ToList(),
                ObtenerProyectoPorIdQuery q => Proyectos.Where(p => p.Id == q.Id).Select(DetalleDe).FirstOrDefault(),
                ObtenerTecnicosProyectoQuery => (IReadOnlyList<TecnicoProyectoDto>)
                [
                    new TecnicoProyectoDto(TecnicoActivoId, Guid.NewGuid(), "Salas Moreno, Javier", "12345678Z",
                        new DateOnly(2026, 6, 2), null, EstaActivo: true),
                    new TecnicoProyectoDto(TecnicoDeBajaId, Guid.NewGuid(), "Duarte, Ana", "49332077T",
                        new DateOnly(2026, 6, 9), new DateOnly(2026, 7, 31), EstaActivo: false),
                ],
                DesasignarTecnicoProyectoCommand => Result.Exito(),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")
            };

            return Task.FromResult((TResponse)respuesta!);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
        {
            Enviados.Add(request!);
            return Task.CompletedTask;
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            return Task.FromResult<object?>(null);
        }

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private readonly MediatorProyectos _mediator = new();

    /// <summary>
    /// Los filtros son [SupplyParameterFromQuery]: se llega a ellos navegando,
    /// no pasándolos como parámetros de componente.
    /// </summary>
    private IRenderedComponent<Proyectos> Renderizar(string ruta = "proyectos")
    {
        Services.AddScoped<IMediator>(_ => _mediator);
        Services.AddScoped<ToastService>();
        Services.GetRequiredService<NavigationManager>().NavigateTo(ruta);
        return Render<Proyectos>();
    }

    private async Task<IRenderedComponent<Proyectos>> RenderizarConClienteAsync(string ruta = "proyectos")
    {
        var cut = Renderizar(ruta);
        await SelectorDeCliente(cut).ChangeAsync(new ChangeEventArgs { Value = ClienteId.ToString() });
        return cut;
    }

    private static IElement SelectorDeCliente(IRenderedComponent<Proyectos> cut) =>
        cut.FindAll("select").Single(s => s.TextContent.Contains("Selecciona un cliente"));

    private static IElement BotonConTexto(IRenderedComponent<Proyectos> cut, string selector, string texto) =>
        cut.FindAll(selector).Single(b => b.TextContent.Trim() == texto);

    private static string ValorDeCampoInfo(IElement ambito, string etiqueta) =>
        ambito.QuerySelectorAll(".campo-info")
            .Single(c => c.QuerySelector(".campo-info-etiqueta")!.TextContent.Trim() == etiqueta)
            .QuerySelector(".campo-info-valor")!.TextContent.Trim();

    private string Uri => Services.GetRequiredService<NavigationManager>().Uri;

    // ------------------------------------------------------------------ estados de la lista

    [Fact]
    public void Sin_cliente_elegido_pide_elegir_uno_y_no_ofrece_crear()
    {
        _mediator.Proyectos = [ProyectoAbierto];

        var cut = Renderizar();

        cut.Markup.Should().Contain("Elige un cliente para ver sus proyectos");
        cut.Markup.Should().NotContain("+ Nuevo proyecto",
            "un proyecto cuelga siempre de un cliente: sin cliente no hay a quién colgarlo");
        _mediator.Enviados.OfType<ObtenerProyectosQuery>().Should().BeEmpty();
    }

    [Fact]
    public async Task Cliente_sin_proyectos_y_sin_filtros_invita_a_crear_el_primero()
    {
        var cut = await RenderizarConClienteAsync();

        cut.Find(".estado-vacio h3").TextContent.Should().Be("Sin proyectos");
        cut.Markup.Should().Contain("Este cliente todavía no tiene ningún proyecto de obra o instalación.");
        cut.Markup.Should().NotContain("Ningún proyecto con este filtro");
    }

    [Fact]
    public async Task Busqueda_sin_coincidencias_no_invita_a_crear_y_ofrece_quitar_los_filtros()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoCerrado];

        var cut = await RenderizarConClienteAsync("proyectos?q=calderas");

        cut.Find(".estado-vacio h3").TextContent.Should().Be("Ningún proyecto con este filtro");
        cut.Find(".estado-vacio button").TextContent.Trim().Should().Be("Quitar los filtros");
        cut.Markup.Should().NotContain("Sin proyectos",
            "mandar a crear a quien acaba de filtrar termina en una obra duplicada");
    }

    /// <summary>
    /// El vacío por filtro afirma que el cliente SÍ tiene proyectos. Solo puede
    /// afirmarlo porque el filtro es en memoria sobre la lista completa; con la
    /// lista vacía y un filtro puesto, lo cierto sigue siendo «Sin proyectos».
    /// </summary>
    [Fact]
    public async Task Con_filtro_puesto_y_cliente_sin_proyectos_no_afirma_que_los_haya()
    {
        var cut = await RenderizarConClienteAsync("proyectos?q=calderas");

        cut.Find(".estado-vacio h3").TextContent.Should().Be("Sin proyectos");
        cut.Markup.Should().NotContain("sí tiene proyectos");
    }

    [Fact]
    public async Task Filtro_de_estado_cerrados_deja_solo_los_cerrados_y_lo_cuenta()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoCerrado];

        var cut = await RenderizarConClienteAsync("proyectos?estado=cerrados");

        var nombres = cut.FindAll("tbody .nombre-proyecto").Select(b => b.TextContent.Trim()).ToList();
        nombres.Should().Equal(ProyectoCerrado.Nombre);
        cut.Find(".conteo-proyectos").TextContent.Trim().Should().Be("1 de 2 proyecto(s) de este cliente");
    }

    [Fact]
    public async Task La_busqueda_encuentra_tambien_por_nombre_de_centro()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoCerrado];

        var cut = await RenderizarConClienteAsync("proyectos?q=portugalete");

        var nombres = cut.FindAll("tbody .nombre-proyecto").Select(b => b.TextContent.Trim()).ToList();
        nombres.Should().Equal(ProyectoCerrado.Nombre);
    }

    [Fact]
    public async Task Quitar_los_filtros_los_limpia_tambien_de_la_url_y_devuelve_la_lista()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoCerrado];
        var cut = await RenderizarConClienteAsync("proyectos?q=calderas&estado=cerrados");
        cut.Find(".estado-vacio h3").TextContent.Should().Be("Ningún proyecto con este filtro", "es el punto de partida de este caso");

        await cut.Find(".estado-vacio button").ClickAsync(new MouseEventArgs());

        Uri.Should().NotContain("q=").And.NotContain("estado=",
            "OnParametersSet re-sincroniza desde la URL: dejar ahí los filtros los devolvería en la siguiente navegación");
        cut.FindAll("tbody .nombre-proyecto").Select(b => b.TextContent.Trim())
            .Should().BeEquivalentTo([ProyectoAbierto.Nombre, ProyectoCerrado.Nombre]);
        SelectorDeCliente(cut).GetAttribute("value").Should().Be(ClienteId.ToString(),
            "el cliente es el maestro de la lista, no un filtro: quitar los filtros no lo toca");
    }

    [Fact]
    public async Task Un_fallo_al_cargar_los_proyectos_se_ofrece_reintentar()
    {
        _mediator.Proyectos = [ProyectoAbierto];
        _mediator.FallarAlCargarProyectos = true;

        var cut = await RenderizarConClienteAsync();

        cut.Find(".estado-vacio h3").TextContent.Should().Be("No pudimos cargar los proyectos");
        cut.Markup.Should().NotContain("Sin proyectos", "un error de carga no es una lista vacía");

        _mediator.FallarAlCargarProyectos = false;
        await BotonConTexto(cut, ".estado-vacio button", "Reintentar").ClickAsync(new MouseEventArgs());

        cut.FindAll("tbody .nombre-proyecto").Select(b => b.TextContent.Trim()).Should().Equal(ProyectoAbierto.Nombre);
    }

    // ------------------------------------------------------------------ panel lateral

    /// <summary>
    /// «Días abiertos» cuenta el día de inicio y el de cierre, igual que la
    /// facturación por días de proyecto abierto: 9 de enero a 4 de marzo de
    /// 2026 son 55 días así contados (el mockup pintaba 54, cuenta exclusiva).
    /// </summary>
    [Fact]
    public async Task El_detalle_se_abre_en_el_panel_lateral_con_los_dias_abiertos_de_facturacion()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoCerrado];
        var cut = await RenderizarConClienteAsync();

        await BotonConTexto(cut, "tbody .nombre-proyecto", ProyectoCerrado.Nombre).ClickAsync(new MouseEventArgs());

        var panel = cut.Find("aside.panel-proyecto");
        panel.TextContent.Should().Contain("Consulta · Proyecto").And.Contain(ProyectoCerrado.Nombre);
        ValorDeCampoInfo(panel, "Estado").Should().Be("Cerrado (04/03/2026)");
        ValorDeCampoInfo(panel, "Días abiertos").Should().Be("55");
    }

    [Fact]
    public async Task Cerrar_proyecto_se_ofrece_en_el_pie_del_panel_solo_si_esta_abierto()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoCerrado];
        var cut = await RenderizarConClienteAsync();

        await BotonConTexto(cut, "tbody .nombre-proyecto", ProyectoCerrado.Nombre).ClickAsync(new MouseEventArgs());
        cut.FindAll(".pie-panel-proyecto button").Select(b => b.TextContent.Trim()).Should().Equal("Editar");

        await BotonConTexto(cut, "tbody .nombre-proyecto", ProyectoAbierto.Nombre).ClickAsync(new MouseEventArgs());
        cut.FindAll(".pie-panel-proyecto button").Select(b => b.TextContent.Trim()).Should().Equal("Editar", "Cerrar proyecto");

        await BotonConTexto(cut, ".pie-panel-proyecto button", "Cerrar proyecto").ClickAsync(new MouseEventArgs());
        cut.Find("[role=dialog] h2").TextContent.Should().Be("Cerrar proyecto");
        _mediator.Enviados.OfType<CerrarProyectoCommand>().Should().BeEmpty("nada se cierra hasta confirmar en el modal");
    }

    /// <summary>
    /// Antes el modal de cierre reutilizaba la selección del detalle: cerrar
    /// desde el menú de una fila, sin detalle abierto, hacía aparecer el
    /// detalle vacío con «No pudimos cargar este proyecto».
    /// </summary>
    [Fact]
    public async Task Cerrar_desde_el_menu_de_la_fila_no_abre_un_detalle_vacio()
    {
        _mediator.Proyectos = [ProyectoAbierto];
        var cut = await RenderizarConClienteAsync();

        await cut.Find("tbody .menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        await BotonConTexto(cut, "[role=menuitem]", "Cerrar proyecto").ClickAsync(new MouseEventArgs());

        cut.Find("[role=dialog] h2").TextContent.Should().Be("Cerrar proyecto", "el modal sí se abre");
        cut.FindAll("aside.panel-proyecto").Should().BeEmpty();
        cut.Markup.Should().NotContain("No pudimos cargar este proyecto");
    }

    /// <summary>
    /// El mockup pinta los técnicos como una lista de solo lectura; el código
    /// ya permitía darlos de baja. Transcribir el mockup al pie de la letra
    /// habría borrado esa acción.
    /// </summary>
    [Fact]
    public async Task Dar_de_baja_a_un_tecnico_activo_se_conserva_en_el_panel()
    {
        _mediator.Proyectos = [ProyectoAbierto];
        var cut = await RenderizarConClienteAsync();
        await BotonConTexto(cut, "tbody .nombre-proyecto", ProyectoAbierto.Nombre).ClickAsync(new MouseEventArgs());

        await BotonConTexto(cut, "[role=tab]", "Técnicos").ClickAsync(new MouseEventArgs());

        var filas = cut.FindAll(".fila-tecnico-proyecto");
        filas.Should().HaveCount(2);
        filas.Single(f => f.TextContent.Contains("Duarte, Ana")).QuerySelector(".baja-tecnico-proyecto")
            .Should().BeNull("un técnico ya dado de baja no se puede volver a dar de baja");

        await cut.Find(".baja-tecnico-proyecto").ClickAsync(new MouseEventArgs());

        _mediator.Enviados.OfType<DesasignarTecnicoProyectoCommand>().Should().ContainSingle()
            .Which.Id.Should().Be(TecnicoActivoId);
    }
}
