using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Proyectos.Commands.ActualizarProyecto;
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
/// técnico). Y las respuestas fuera de orden: con el mediador reteniendo
/// peticiones, una respuesta tardía de un detalle, de unos técnicos o de la
/// carga de un cliente que ya no es el vigente no se pinta ni decide sobre
/// qué id opera la pantalla.
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

    private static readonly Guid ClienteBId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly Guid Abierto2Id = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid ProyectoDeBId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private static readonly ProyectoListaDto ProyectoAbierto2 = new(
        Abierto2Id, "Reforma de vestuarios — planta 1", Guid.NewGuid(), "Centro Logístico Norte",
        new DateOnly(2026, 7, 1), null, null, EstaAbierto: true);

    private static readonly ProyectoListaDto ProyectoDeB = new(
        ProyectoDeBId, "Montaje de cámaras de congelación", Guid.NewGuid(), "Planta Barakaldo",
        new DateOnly(2026, 5, 4), null, null, EstaAbierto: true);

    private static readonly CentroSelectorDto CentroDeA = new(Guid.NewGuid(), "Centro Logístico Norte", "Refrielectric S.L.", "Refrielectric S.L.");
    private static readonly CentroSelectorDto CentroDeB = new(Guid.NewGuid(), "Planta Barakaldo", "Frigoríficos Arcos S.A.", "Frigoríficos Arcos S.A.");

    /// <summary>
    /// Mediador que responde por tipo y registra todo lo que se le envía. Una
    /// petición que case con una <see cref="Retener"/> no responde hasta que el
    /// test resuelva su <see cref="TaskCompletionSource{TResult}"/>: así se
    /// ordenan a mano las respuestas de dos peticiones en vuelo.
    /// </summary>
    private sealed class MediatorProyectos : IMediator
    {
        public List<ProyectoListaDto> Proyectos { get; set; } = [];
        public List<ProyectoListaDto> ProyectosClienteB { get; set; } = [];
        public List<CentroSelectorDto> CentrosClienteA { get; set; } = [];
        public List<CentroSelectorDto> CentrosClienteB { get; set; } = [];
        public Dictionary<Guid, IReadOnlyList<TecnicoProyectoDto>> TecnicosPorProyecto { get; } = [];
        public bool FallarAlCargarProyectos { get; set; }
        public List<object> Enviados { get; } = [];

        private readonly List<(Func<object, bool> Cuando, TaskCompletionSource<object?> Respuesta)> _retenciones = [];
        private readonly HashSet<Guid> _tecnicosDadosDeBaja = [];

        public TaskCompletionSource<object?> Retener(Func<object, bool> cuando)
        {
            var respuesta = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _retenciones.Add((cuando, respuesta));
            return respuesta;
        }

        private static async Task<TResponse> Esperar<TResponse>(Task<object?> respuesta) => (TResponse)(await respuesta)!;

        private IReadOnlyList<TecnicoProyectoDto> TecnicosPorDefecto() =>
        [
            _tecnicosDadosDeBaja.Contains(TecnicoActivoId)
                ? new TecnicoProyectoDto(TecnicoActivoId, Guid.NewGuid(), "Salas Moreno, Javier", "12345678Z",
                    new DateOnly(2026, 6, 2), new DateOnly(2026, 9, 11), EstaActivo: false)
                : new TecnicoProyectoDto(TecnicoActivoId, Guid.NewGuid(), "Salas Moreno, Javier", "12345678Z",
                    new DateOnly(2026, 6, 2), null, EstaActivo: true),
            new TecnicoProyectoDto(TecnicoDeBajaId, Guid.NewGuid(), "Duarte, Ana", "49332077T",
                new DateOnly(2026, 6, 9), new DateOnly(2026, 7, 31), EstaActivo: false),
        ];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);

            var retencion = _retenciones.FirstOrDefault(r => r.Cuando(request));
            if (retencion.Respuesta is not null)
            {
                _retenciones.Remove(retencion);
                return Esperar<TResponse>(retencion.Respuesta.Task);
            }

            if (request is DesasignarTecnicoProyectoCommand baja)
                _tecnicosDadosDeBaja.Add(baja.Id);

            object? respuesta = request switch
            {
                ObtenerClientesParaSelectorQuery => new[]
                {
                    new ClienteSelectorDto(ClienteId, "Refrielectric S.L."),
                    new ClienteSelectorDto(ClienteBId, "Frigoríficos Arcos S.A."),
                },
                ObtenerCentrosParaSelectorQuery q => (IReadOnlyList<CentroSelectorDto>)(q.ClienteId == ClienteBId ? CentrosClienteB : CentrosClienteA).ToList(),
                ObtenerProyectosQuery when FallarAlCargarProyectos => throw new InvalidOperationException("fallo simulado de la consulta"),
                ObtenerProyectosQuery q => (IReadOnlyList<ProyectoListaDto>)(q.ClienteId == ClienteBId ? ProyectosClienteB : Proyectos).ToList(),
                ObtenerProyectoPorIdQuery q => Proyectos.Concat(ProyectosClienteB).Where(p => p.Id == q.Id).Select(DetalleDe).FirstOrDefault(),
                ObtenerTecnicosProyectoQuery q => TecnicosPorProyecto.GetValueOrDefault(q.ProyectoId) ?? TecnicosPorDefecto(),
                DesasignarTecnicoProyectoCommand => Result.Exito(),
                CerrarProyectoCommand => Result.Exito(),
                ActualizarProyectoCommand => Result.Exito(),
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

    /// <summary>Sin <c>await</c> a propósito: la tarea termina cuando termina el manejador, respuestas retenidas incluidas.</summary>
    private static Task ElegirCliente(IRenderedComponent<Proyectos> cut, Guid clienteId) =>
        SelectorDeCliente(cut).ChangeAsync(new ChangeEventArgs { Value = clienteId.ToString() });

    private static IElement BotonConTexto(IRenderedComponent<Proyectos> cut, string selector, string texto) =>
        cut.FindAll(selector).Single(b => b.TextContent.Trim() == texto);

    private static Task AbrirDetalle(IRenderedComponent<Proyectos> cut, ProyectoListaDto proyecto) =>
        BotonConTexto(cut, "tbody .nombre-proyecto", proyecto.Nombre).ClickAsync(new MouseEventArgs());

    private static async Task CerrarDesdeLaFilaAsync(IRenderedComponent<Proyectos> cut, ProyectoListaDto proyecto)
    {
        var fila = cut.FindAll("tbody tr").Single(f => f.QuerySelector(".nombre-proyecto")!.TextContent.Trim() == proyecto.Nombre);
        await fila.QuerySelector(".menu-acciones-disparador")!.ClickAsync(new MouseEventArgs());
        await BotonConTexto(cut, "[role=menuitem]", "Cerrar proyecto").ClickAsync(new MouseEventArgs());
    }

    /// <summary>El botón que confirma, dentro del modal: el pie del panel lleva otro con el mismo texto.</summary>
    private static Task ConfirmarCierre(IRenderedComponent<Proyectos> cut) =>
        BotonConTexto(cut, "[role=dialog] .modal-pie button", "Cerrar proyecto").ClickAsync(new MouseEventArgs());

    private static List<string> NombresEnLaTabla(IRenderedComponent<Proyectos> cut) =>
        cut.FindAll("tbody .nombre-proyecto").Select(b => b.TextContent.Trim()).ToList();

    /// <summary>Resuelve una respuesta retenida dentro del despachador del renderizador, como llegaría del servidor.</summary>
    private static Task Resolver(IRenderedComponent<Proyectos> cut, TaskCompletionSource<object?> retenida, object? valor) =>
        cut.InvokeAsync(() => retenida.SetResult(valor));

    private static readonly TimeSpan Paciencia = TimeSpan.FromSeconds(10);

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
        // El cerrado va primero en la lista a propósito: un pie que cerrase
        // "el primero de la lista" en vez del que muestra el panel no pasaría.
        _mediator.Proyectos = [ProyectoCerrado, ProyectoAbierto];
        var cut = await RenderizarConClienteAsync();

        await AbrirDetalle(cut, ProyectoCerrado);
        cut.FindAll(".pie-panel-proyecto button").Select(b => b.TextContent.Trim()).Should().Equal("Editar");

        await AbrirDetalle(cut, ProyectoAbierto);
        cut.FindAll(".pie-panel-proyecto button").Select(b => b.TextContent.Trim()).Should().Equal("Editar", "Cerrar proyecto");

        await BotonConTexto(cut, ".pie-panel-proyecto button", "Cerrar proyecto").ClickAsync(new MouseEventArgs());
        cut.Find("[role=dialog] h2").TextContent.Should().Be("Cerrar proyecto");
        _mediator.Enviados.OfType<CerrarProyectoCommand>().Should().BeEmpty("nada se cierra hasta confirmar en el modal");

        await ConfirmarCierre(cut);

        _mediator.Enviados.OfType<CerrarProyectoCommand>().Should().ContainSingle()
            .Which.Id.Should().Be(AbiertoId, "se cierra el proyecto que muestra el panel");
    }

    /// <summary>
    /// El modal guarda su propio id a cerrar (<c>_idACerrar</c>), separado de
    /// la selección del detalle: cerrar desde la fila de A con el detalle de B
    /// abierto cierra A, no B.
    /// </summary>
    [Fact]
    public async Task Cerrar_una_fila_con_el_detalle_de_otro_proyecto_abierto_cierra_la_fila()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoAbierto2];
        var cut = await RenderizarConClienteAsync();
        await AbrirDetalle(cut, ProyectoAbierto2);

        await CerrarDesdeLaFilaAsync(cut, ProyectoAbierto);
        await ConfirmarCierre(cut);

        _mediator.Enviados.OfType<CerrarProyectoCommand>().Should().ContainSingle()
            .Which.Id.Should().Be(AbiertoId, "el comando lleva el id de la fila, no el del detalle abierto");
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

        // El doble devuelve la lista con la baja aplicada: la pantalla tiene que recargarla.
        cut.FindAll(".baja-tecnico-proyecto").Should().BeEmpty("tras la baja se recargan los técnicos y ya no queda ninguno activo");
        cut.FindAll(".fila-tecnico-proyecto").Single(f => f.TextContent.Contains("Salas Moreno, Javier"))
            .TextContent.Should().Contain("De baja").And.Contain("baja 11/09/2026");
    }

    // ------------------------------------------------------------------ respuestas fuera de orden

    /// <summary>
    /// Pulsar el detalle de A y enseguida el de B: si A responde después, el
    /// panel de B no puede pintar A, y Cerrar —que usa el id del detalle— no
    /// puede operar sobre A.
    /// </summary>
    [Fact]
    public async Task Un_detalle_que_responde_tarde_no_pisa_el_proyecto_elegido_despues()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoAbierto2];
        var cut = await RenderizarConClienteAsync();
        var detalleA = _mediator.Retener(r => r is ObtenerProyectoPorIdQuery q && q.Id == AbiertoId);
        var detalleB = _mediator.Retener(r => r is ObtenerProyectoPorIdQuery q && q.Id == Abierto2Id);

        var clicA = AbrirDetalle(cut, ProyectoAbierto);
        var clicB = AbrirDetalle(cut, ProyectoAbierto2);
        await Resolver(cut, detalleB, DetalleDe(ProyectoAbierto2));
        await clicB.WaitAsync(Paciencia);
        await Resolver(cut, detalleA, DetalleDe(ProyectoAbierto));
        await clicA.WaitAsync(Paciencia);

        cut.Find(".nombre-cabecera-panel-proyecto").TextContent.Trim().Should().Be(ProyectoAbierto2.Nombre,
            "la respuesta tardía de A pertenece a una selección que ya no es la vigente");

        await BotonConTexto(cut, ".pie-panel-proyecto button", "Cerrar proyecto").ClickAsync(new MouseEventArgs());
        await ConfirmarCierre(cut);
        _mediator.Enviados.OfType<CerrarProyectoCommand>().Should().ContainSingle()
            .Which.Id.Should().Be(Abierto2Id, "Cerrar opera sobre el proyecto que el panel muestra");
    }

    /// <summary>
    /// Cerrar el panel también invalida el detalle en vuelo. Si no, la
    /// respuesta tardía deja <c>_detalle</c> apuntando a A con el panel
    /// cerrado, y cerrar después A desde su fila vuelve a abrir el panel solo
    /// (ConfirmarCerrarAsync refresca el detalle si es el del proyecto cerrado).
    /// </summary>
    [Fact]
    public async Task Un_detalle_que_responde_tras_cerrar_el_panel_no_lo_reabre()
    {
        _mediator.Proyectos = [ProyectoAbierto];
        var cut = await RenderizarConClienteAsync();
        var detalleA = _mediator.Retener(r => r is ObtenerProyectoPorIdQuery q && q.Id == AbiertoId);

        var clicA = AbrirDetalle(cut, ProyectoAbierto);
        await cut.Find(".cerrar-panel-proyecto").ClickAsync(new MouseEventArgs());
        await Resolver(cut, detalleA, DetalleDe(ProyectoAbierto));
        await clicA.WaitAsync(Paciencia);

        await CerrarDesdeLaFilaAsync(cut, ProyectoAbierto);
        await ConfirmarCierre(cut);

        _mediator.Enviados.OfType<CerrarProyectoCommand>().Should().ContainSingle().Which.Id.Should().Be(AbiertoId);
        cut.FindAll("aside.panel-proyecto").Should().BeEmpty("el panel se cerró a mano y nadie ha vuelto a abrirlo");
    }

    /// <summary>
    /// Guardar la edición de A y, con el guardado en vuelo, abrir B: el
    /// guardado correcto no puede acabar en error por leer el detalle que ya
    /// no es el suyo (antes <c>_detalle.Id</c>, nulo mientras B carga), y
    /// tampoco devolver el panel a A. La lista sí se recarga.
    /// </summary>
    [Fact]
    public async Task Guardar_la_edicion_con_otro_proyecto_ya_elegido_recarga_la_lista_sin_volver_al_editado()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoAbierto2];
        var cut = await RenderizarConClienteAsync();
        await AbrirDetalle(cut, ProyectoAbierto);
        await BotonConTexto(cut, ".pie-panel-proyecto button", "Editar").ClickAsync(new MouseEventArgs());
        var guardado = _mediator.Retener(r => r is ActualizarProyectoCommand c && c.Id == AbiertoId);
        var detalleB = _mediator.Retener(r => r is ObtenerProyectoPorIdQuery q && q.Id == Abierto2Id);

        var guardar = BotonConTexto(cut, ".acciones-formulario button", "Guardar").ClickAsync(new MouseEventArgs());
        var clicB = AbrirDetalle(cut, ProyectoAbierto2);
        var enviadosAntes = _mediator.Enviados.Count;
        await Resolver(cut, guardado, Result.Exito());
        await guardar.WaitAsync(Paciencia);
        await Resolver(cut, detalleB, DetalleDe(ProyectoAbierto2));
        await clicB.WaitAsync(Paciencia);

        var trasGuardar = _mediator.Enviados.Skip(enviadosAntes).ToList();
        trasGuardar.OfType<ObtenerProyectosQuery>().Should().ContainSingle("tras un guardado correcto se recarga la lista");
        trasGuardar.OfType<ObtenerProyectoPorIdQuery>().Should().BeEmpty("el proyecto editado ya no es el abierto");
        cut.Find(".nombre-cabecera-panel-proyecto").TextContent.Trim().Should().Be(ProyectoAbierto2.Nombre);
    }

    /// <summary>
    /// Los técnicos pedidos para A que llegan con el detalle de B ya abierto
    /// no se pintan en B: la pestaña Técnicos de B pide y enseña los suyos.
    /// </summary>
    [Fact]
    public async Task Tecnicos_que_responden_tarde_no_aparecen_en_el_detalle_siguiente()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoAbierto2];
        _mediator.TecnicosPorProyecto[Abierto2Id] =
        [
            new TecnicoProyectoDto(Guid.NewGuid(), Guid.NewGuid(), "Iglesias Ruiz, Marta", "70112233K",
                new DateOnly(2026, 7, 1), null, EstaActivo: true),
        ];
        var cut = await RenderizarConClienteAsync();
        await AbrirDetalle(cut, ProyectoAbierto);
        var tecnicosA = _mediator.Retener(r => r is ObtenerTecnicosProyectoQuery q && q.ProyectoId == AbiertoId);

        var pestanaA = BotonConTexto(cut, "[role=tab]", "Técnicos").ClickAsync(new MouseEventArgs());
        await AbrirDetalle(cut, ProyectoAbierto2);
        await Resolver(cut, tecnicosA, (IReadOnlyList<TecnicoProyectoDto>)
        [
            new TecnicoProyectoDto(TecnicoActivoId, Guid.NewGuid(), "Salas Moreno, Javier", "12345678Z",
                new DateOnly(2026, 6, 2), null, EstaActivo: true),
        ]);
        await pestanaA.WaitAsync(Paciencia);

        await BotonConTexto(cut, "[role=tab]", "Técnicos").ClickAsync(new MouseEventArgs());

        cut.FindAll(".fila-tecnico-proyecto .nombre-tecnico-proyecto").Select(n => n.TextContent.Trim())
            .Should().Equal(["Iglesias Ruiz, Marta"], "son los técnicos del proyecto abierto, no los del anterior");
    }

    [Fact]
    public async Task Los_proyectos_del_cliente_anterior_que_llegan_tarde_no_se_pintan_bajo_el_nuevo()
    {
        _mediator.Proyectos = [ProyectoAbierto];
        _mediator.ProyectosClienteB = [ProyectoDeB];
        var cut = Renderizar();
        var proyectosA = _mediator.Retener(r => r is ObtenerProyectosQuery q && q.ClienteId == ClienteId);
        var proyectosB = _mediator.Retener(r => r is ObtenerProyectosQuery q && q.ClienteId == ClienteBId);

        var cambioA = ElegirCliente(cut, ClienteId);
        var cambioB = ElegirCliente(cut, ClienteBId);
        await Resolver(cut, proyectosB, (IReadOnlyList<ProyectoListaDto>)[ProyectoDeB]);
        await cambioB.WaitAsync(Paciencia);
        await Resolver(cut, proyectosA, (IReadOnlyList<ProyectoListaDto>)[ProyectoAbierto]);
        await cambioA.WaitAsync(Paciencia);

        NombresEnLaTabla(cut).Should().Equal([ProyectoDeB.Nombre], "el cliente vigente es B");
    }

    /// <summary>
    /// El orden inverso: A responde mientras B sigue cargando. La respuesta de
    /// A no puede ni pintar su lista ni dar por terminada la carga de B (lo
    /// que enseñaría «Sin proyectos» de un cliente que todavía no ha contestado).
    /// </summary>
    [Fact]
    public async Task Una_carga_del_cliente_anterior_no_da_por_terminada_la_del_nuevo()
    {
        _mediator.Proyectos = [ProyectoAbierto];
        var cut = Renderizar();
        var proyectosA = _mediator.Retener(r => r is ObtenerProyectosQuery q && q.ClienteId == ClienteId);
        var proyectosB = _mediator.Retener(r => r is ObtenerProyectosQuery q && q.ClienteId == ClienteBId);

        var cambioA = ElegirCliente(cut, ClienteId);
        var cambioB = ElegirCliente(cut, ClienteBId);
        await Resolver(cut, proyectosA, (IReadOnlyList<ProyectoListaDto>)[ProyectoAbierto]);
        await cambioA.WaitAsync(Paciencia);

        cut.FindAll(".esqueleto-lista").Should().NotBeEmpty("la carga de B sigue en curso");
        cut.Markup.Should().NotContain("Sin proyectos");
        NombresEnLaTabla(cut).Should().BeEmpty();

        await Resolver(cut, proyectosB, (IReadOnlyList<ProyectoListaDto>)[ProyectoDeB]);
        await cambioB.WaitAsync(Paciencia);
        NombresEnLaTabla(cut).Should().Equal([ProyectoDeB.Nombre]);
    }

    [Fact]
    public async Task Los_centros_del_cliente_anterior_que_llegan_tarde_no_llegan_al_alta_del_nuevo()
    {
        _mediator.CentrosClienteA = [CentroDeA];
        _mediator.CentrosClienteB = [CentroDeB];
        _mediator.ProyectosClienteB = [ProyectoDeB];
        var cut = Renderizar();
        var centrosA = _mediator.Retener(r => r is ObtenerCentrosParaSelectorQuery q && q.ClienteId == ClienteId);

        var cambioA = ElegirCliente(cut, ClienteId);
        await ElegirCliente(cut, ClienteBId).WaitAsync(Paciencia);
        await Resolver(cut, centrosA, (IReadOnlyList<CentroSelectorDto>)[CentroDeA]);
        await cambioA.WaitAsync(Paciencia);

        await cut.FindAll("button").First(b => b.TextContent.Trim() == "+ Nuevo proyecto").ClickAsync(new MouseEventArgs());
        var centrosOfrecidos = cut.FindAll("option").Select(o => o.TextContent.Trim()).ToList();
        centrosOfrecidos.Should().Contain(CentroDeB.Nombre)
            .And.NotContain(CentroDeA.Nombre, "el alta cuelga del cliente vigente: un centro de A lo colgaría del cliente equivocado");
    }

    [Fact]
    public async Task Un_fallo_del_cliente_anterior_que_llega_tarde_no_se_pinta_bajo_el_nuevo()
    {
        _mediator.ProyectosClienteB = [ProyectoDeB];
        var cut = Renderizar();
        var proyectosA = _mediator.Retener(r => r is ObtenerProyectosQuery q && q.ClienteId == ClienteId);

        var cambioA = ElegirCliente(cut, ClienteId);
        await ElegirCliente(cut, ClienteBId).WaitAsync(Paciencia);
        await cut.InvokeAsync(() => proyectosA.SetException(new InvalidOperationException("fallo simulado de A")));
        await cambioA.WaitAsync(Paciencia);

        cut.Markup.Should().NotContain("No pudimos cargar los proyectos", "el fallo es de A y el cliente vigente es B");
        NombresEnLaTabla(cut).Should().Equal([ProyectoDeB.Nombre]);
    }

    /// <summary>La recarga tras cerrar un proyecto de A, si responde con B ya elegido, tampoco pinta A.</summary>
    [Fact]
    public async Task La_recarga_tras_cerrar_no_pinta_el_cliente_anterior_si_ya_se_cambio()
    {
        _mediator.Proyectos = [ProyectoAbierto];
        _mediator.ProyectosClienteB = [ProyectoDeB];
        var cut = await RenderizarConClienteAsync();
        var recargaA = _mediator.Retener(r => r is ObtenerProyectosQuery q && q.ClienteId == ClienteId);

        await CerrarDesdeLaFilaAsync(cut, ProyectoAbierto);
        var confirmacion = ConfirmarCierre(cut);
        await ElegirCliente(cut, ClienteBId).WaitAsync(Paciencia);
        await Resolver(cut, recargaA, (IReadOnlyList<ProyectoListaDto>)[ProyectoAbierto]);
        await confirmacion.WaitAsync(Paciencia);

        NombresEnLaTabla(cut).Should().Equal([ProyectoDeB.Nombre], "la recarga era de A y el cliente vigente es B");
    }
}
