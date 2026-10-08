using AngleSharp.Dom;
using CaeManager.Infrastructure.Identity;
using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Proyectos.Commands.ActualizarProyecto;
using CaeManager.Application.Proyectos.Commands.CerrarProyecto;
using CaeManager.Application.Proyectos.Commands.CrearProyecto;
using CaeManager.Application.Proyectos.Commands.DesasignarTecnicoProyecto;
using CaeManager.Application.Proyectos.Commands.EliminarProyecto;
using CaeManager.Application.Proyectos.Commands.RestaurarProyecto;
using CaeManager.Application.Proyectos.Commands.ReabrirProyecto;
using CaeManager.Application.Proyectos.Queries.ObtenerProyectoPorId;
using CaeManager.Application.Proyectos.Queries.ObtenerProyectos;
using CaeManager.Application.Proyectos.Queries.ObtenerTecnicosProyecto;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
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
    public ProyectosGen2Tests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        this.ConRolDeEscritura();
    }

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
                ? new TecnicoProyectoDto(TecnicoActivoId, Guid.NewGuid(), "Salas Moreno, Javier",
                    new DateOnly(2026, 6, 2), new DateOnly(2026, 9, 11), EstaActivo: false)
                : new TecnicoProyectoDto(TecnicoActivoId, Guid.NewGuid(), "Salas Moreno, Javier",
                    new DateOnly(2026, 6, 2), null, EstaActivo: true),
            new TecnicoProyectoDto(TecnicoDeBajaId, Guid.NewGuid(), "Duarte, Ana",
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

            if (request is EliminarProyectoCommand eliminado)
                Proyectos.RemoveAll(p => p.Id == eliminado.Id);

            object? respuesta = request switch
            {
                ObtenerClientesAutorizadosQuery => (IReadOnlyList<ClienteAutorizadoDto>)[],
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
                ObtenerTrabajadoresParaSelectorQuery => (IReadOnlyList<TrabajadorSelectorDto>)[new TrabajadorSelectorDto(Guid.NewGuid(), "Salas Moreno, Javier", null, null)],
                DesasignarTecnicoProyectoCommand => Result.Exito(),
                EliminarProyectoCommand => Result.Exito(),
                RestaurarProyectoCommand => Result.Exito(),
                CerrarProyectoCommand => Result.Exito(),
                CrearProyectoCommand => Result.Exito(Guid.NewGuid()),
                ReabrirProyectoCommand => Result.Exito(),
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
        Services.AddScoped<ITenantActual>(_ => new SeleccionEmpresaGestionadaDePrueba());
        Services.GetRequiredService<NavigationManager>().NavigateTo(ruta);
        return Render<Proyectos>();
    }

    private async Task<IRenderedComponent<Proyectos>> RenderizarConClienteAsync(string ruta = "proyectos")
    {
        var cut = Renderizar(ruta);
        await ElegirCliente(cut, ClienteId);
        return cut;
    }

    private static IElement SelectorDeCliente(IRenderedComponent<Proyectos> cut) =>
        DisparadorPastilla(cut, "Cliente empresarial");

    // Devuelve la tarea del clic: las pruebas de respuestas retenidas y confirmaciones
    // siguen observando todo el manejador, sin invocarlo por reflexión ni saltarse el menú.
    private static Task ElegirCliente(IRenderedComponent<Proyectos> cut, Guid clienteId) =>
        ElegirPastillaAsync(cut, "Cliente empresarial", clienteId == Guid.Empty
            ? "Selecciona un Cliente empresarial"
            : clienteId == ClienteId ? "Refrielectric S.L." : "Frigoríficos Arcos S.A.");

    private static IElement DisparadorPastilla(IRenderedComponent<Proyectos> cut, string etiqueta) =>
        cut.FindAll(".barra-filtros-pastillas [aria-haspopup=menu]")
            .Single(b => (b.GetAttribute("aria-label") ?? "").StartsWith(etiqueta, StringComparison.Ordinal));

    private static async Task ElegirPastillaAsync(IRenderedComponent<Proyectos> cut, string etiqueta, string opcion)
    {
        var disparador = DisparadorPastilla(cut, etiqueta);
        if (disparador.GetAttribute("aria-expanded") != "true")
            await disparador.ClickAsync(new MouseEventArgs());

        var panelId = DisparadorPastilla(cut, etiqueta).GetAttribute("aria-controls")!;
        var panel = cut.Find("#" + panelId);
        var item = panel.QuerySelectorAll("[role=menuitemradio]").Single(i => i.TextContent.Trim() == opcion);
        await item.ClickAsync(new MouseEventArgs());
    }

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

    // ------------------------------------------------------------------ reabrir (FS-12)

    /// <summary>
    /// FS-12 (auditoría de flujos sin salida, 2026-09-24): tras «Cerrar proyecto» no
    /// quedaba ninguna acción, y un cierre con fecha equivocada afecta a la
    /// facturación por días. El proyecto cerrado ofrece «Reabrir proyecto» en el pie
    /// del panel, y reabre justo el que muestra el panel. Reabrir borra la fecha de
    /// cierre, así que antes se confirma nombrándola y sin confirmar no se envía nada.
    /// </summary>
    [Fact]
    public async Task Un_proyecto_cerrado_se_reabre_desde_el_pie_del_panel_tras_confirmar_la_fecha_que_se_pierde()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoCerrado];
        var cut = await RenderizarConClienteAsync();
        await AbrirDetalle(cut, ProyectoCerrado);

        await BotonConTexto(cut, ".pie-panel-proyecto button", "Reabrir proyecto").ClickAsync(new MouseEventArgs());

        _mediator.Enviados.OfType<ReabrirProyectoCommand>().Should().BeEmpty("el primer clic solo pide confirmación");
        cut.Find("[role=dialog]").TextContent.Should()
            .Contain(ProyectoCerrado.Nombre)
            .And.Contain(ProyectoCerrado.FechaCierreReal!.Value.ToString(), "la confirmación nombra la fecha de cierre que se pierde");

        await ConfirmarReapertura(cut);

        _mediator.Enviados.OfType<ReabrirProyectoCommand>().Should().ContainSingle()
            .Which.Id.Should().Be(ProyectoCerrado.Id, "se reabre el proyecto que muestra el panel");
        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle(m => m.Tono == TonoToast.Exito)
            .Which.Mensaje.Should().StartWith("Proyecto reabierto");
    }

    [Fact]
    public async Task Un_proyecto_cerrado_se_reabre_desde_el_menu_de_su_fila_y_uno_abierto_no_lo_ofrece()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoCerrado];
        var cut = await RenderizarConClienteAsync();

        async Task<List<string>> OpcionesDeLaFila(ProyectoListaDto proyecto)
        {
            var fila = cut.FindAll("tbody tr").Single(f => f.QuerySelector(".nombre-proyecto")!.TextContent.Trim() == proyecto.Nombre);
            await fila.QuerySelector(".menu-acciones-disparador")!.ClickAsync(new MouseEventArgs());
            // Releer la fila tras el clic: el menú se pinta dentro de ella.
            fila = cut.FindAll("tbody tr").Single(f => f.QuerySelector(".nombre-proyecto")!.TextContent.Trim() == proyecto.Nombre);
            return fila.QuerySelectorAll("[role=menuitem]").Select(m => m.TextContent.Trim()).ToList();
        }

        (await OpcionesDeLaFila(ProyectoAbierto)).Should().Contain("Cerrar proyecto").And.NotContain("Reabrir proyecto");
        (await OpcionesDeLaFila(ProyectoCerrado)).Should().Contain("Reabrir proyecto").And.NotContain("Cerrar proyecto");

        await cut.FindAll("[role=menuitem]").Single(m => m.TextContent.Trim() == "Reabrir proyecto").ClickAsync(new MouseEventArgs());
        await ConfirmarReapertura(cut);

        _mediator.Enviados.OfType<ReabrirProyectoCommand>().Should().ContainSingle()
            .Which.Id.Should().Be(ProyectoCerrado.Id);
    }

    [Fact]
    public async Task Consulta_no_ve_reabrir_en_un_proyecto_cerrado()
    {
        this.ConRolDeEscritura(Roles.Consulta);
        _mediator.Proyectos = [ProyectoCerrado];
        var cut = await RenderizarConClienteAsync();
        await AbrirDetalle(cut, ProyectoCerrado);

        cut.FindAll("aside.panel-proyecto button").Select(b => b.TextContent.Trim()).Should().NotContain("Reabrir proyecto");

        var fila = cut.Find("tbody tr");
        await fila.QuerySelector(".menu-acciones-disparador")!.ClickAsync(new MouseEventArgs());
        var opciones = cut.Find("tbody tr").QuerySelectorAll("[role=menuitem]").Select(m => m.TextContent.Trim()).ToList();
        opciones.Should().NotBeEmpty("el menú de la fila tiene que haberse abierto para que su ausencia signifique algo");
        opciones.Should().NotContain("Reabrir proyecto");
    }

    private static Task ConfirmarReapertura(IRenderedComponent<Proyectos> cut) =>
        BotonConTexto(cut, "[role=dialog] .modal-pie button", "Reabrir proyecto").ClickAsync(new MouseEventArgs());

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

    // Fase 1: adaptación del contexto obligatorio y de las celdas combinadas.
    [Fact]
    public async Task Volver_a_elegir_Cliente_empresarial_no_consulta_todos_y_reabre_un_contexto_concreto()
    {
        _mediator.Proyectos = [ProyectoAbierto];
        _mediator.ProyectosClienteB = [ProyectoDeB];
        var cut = await RenderizarConClienteAsync();
        var consultasAntes = _mediator.Enviados.OfType<ObtenerProyectosQuery>().Count();
        consultasAntes.Should().BeGreaterThan(0, "control positivo: había un Cliente empresarial elegido y datos cargados");

        await SelectorDeCliente(cut).ClickAsync(new MouseEventArgs());
        var panelId = SelectorDeCliente(cut).GetAttribute("aria-controls")!;
        var opciones = cut.Find("#" + panelId).QuerySelectorAll("[role=menuitemradio]").Select(i => i.TextContent.Trim()).ToList();
        opciones.Should().Equal("Selecciona un Cliente empresarial", "Refrielectric S.L.", "Frigoríficos Arcos S.A.");
        await ElegirCliente(cut, Guid.Empty);

        cut.Markup.Should().Contain("Elige un Cliente empresarial para ver sus proyectos");
        cut.FindAll("tbody .nombre-proyecto").Should().BeEmpty();
        cut.Markup.Should().NotContain("+ Nuevo proyecto");
        _mediator.Enviados.OfType<ObtenerProyectosQuery>().Count().Should().Be(consultasAntes,
            "Guid.Empty pide elegir, nunca consulta todos los Clientes empresariales");

        await ElegirCliente(cut, ClienteBId);
        _mediator.Enviados.OfType<ObtenerProyectosQuery>().Last().ClienteId.Should().Be(ClienteBId);
        NombresEnLaTabla(cut).Should().Equal(ProyectoDeB.Nombre);
    }

    [Fact]
    public async Task Las_celdas_combinadas_conservan_fechas_orden_recibido_y_detalles_por_Id()
    {
        // Fixture existente en orden FechaInicio descendente; orden por Nombre lo alteraría.
        _mediator.Proyectos = [ProyectoAbierto2, ProyectoAbierto, ProyectoCerrado];
        var cut = await RenderizarConClienteAsync();
        _mediator.Enviados.OfType<ObtenerProyectosQuery>().Last().ClienteId.Should().Be(ClienteId);
        NombresEnLaTabla(cut).Should().Equal(ProyectoAbierto2.Nombre, ProyectoAbierto.Nombre, ProyectoCerrado.Nombre);
        var filas = cut.FindAll("tbody tr");
        filas[1].QuerySelector(".proyecto-centro")!.TextContent.Trim().Should().Be(ProyectoAbierto.CentroNombre);
        var plazoConFin = filas[1].QuerySelector(".proyecto-plazo")!.TextContent;
        plazoConFin.Should().Contain(ProyectoAbierto.FechaInicio.ToString("dd/MM/yyyy"))
            .And.Contain(ProyectoAbierto.FechaFinPrevista!.Value.ToString("dd/MM/yyyy"));
        var plazoSinFin = filas[0].QuerySelector(".proyecto-plazo")!.TextContent;
        plazoSinFin.Should().Contain(ProyectoAbierto2.FechaInicio.ToString("dd/MM/yyyy"))
            .And.Contain("Sin fecha de fin prevista");

        await AbrirDetalle(cut, ProyectoAbierto);
        _mediator.Enviados.OfType<ObtenerProyectoPorIdQuery>().Last().Id.Should().Be(ProyectoAbierto.Id);
        cut.Find(".nombre-cabecera-panel-proyecto").TextContent.Trim().Should().Be(ProyectoAbierto.Nombre);
    }
    // ------------------------------------------------------------------ estados de la lista

    [Fact]
    public void Sin_cliente_elegido_pide_elegir_uno_y_no_ofrece_crear()
    {
        _mediator.Proyectos = [ProyectoAbierto];

        var cut = Renderizar();

        cut.Markup.Should().Contain("Elige un Cliente empresarial para ver sus proyectos");
        cut.Markup.Should().NotContain("+ Nuevo proyecto",
            "un proyecto cuelga siempre de un cliente: sin cliente no hay a quién colgarlo");
        _mediator.Enviados.OfType<ObtenerProyectosQuery>().Should().BeEmpty();
    }

    [Fact]
    public async Task Cliente_sin_proyectos_y_sin_filtros_invita_a_crear_el_primero()
    {
        var cut = await RenderizarConClienteAsync();

        cut.Find(".estado-vacio h3").TextContent.Should().Be("Sin proyectos");
        cut.Markup.Should().Contain("Este Cliente empresarial todavía no tiene ningún proyecto de obra o instalación.");
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
        cut.Find(".cabecera-listado-contador").TextContent.Trim().Should().Be("1");
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
        SelectorDeCliente(cut).GetAttribute("aria-label").Should().Be("Cliente empresarial: Refrielectric S.L.",
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
        cut.FindAll(".pie-panel-proyecto button").Select(b => b.TextContent.Trim()).Should().Equal("Editar", "Reabrir proyecto", "Eliminar");

        await AbrirDetalle(cut, ProyectoAbierto);
        cut.FindAll(".pie-panel-proyecto button").Select(b => b.TextContent.Trim()).Should().Equal("Editar", "Cerrar proyecto", "Eliminar");

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
    /// <summary>
    /// Dar de baja a un técnico no se deshace desde la aplicación: cancelar el
    /// diálogo no envía <see cref="DesasignarTecnicoProyectoCommand"/> y el
    /// técnico sigue activo.
    /// </summary>
    [Fact]
    public async Task Cancelar_la_baja_de_un_tecnico_no_envia_el_Command()
    {
        _mediator.Proyectos = [ProyectoAbierto];
        var cut = await RenderizarConClienteAsync();
        await BotonConTexto(cut, "tbody .nombre-proyecto", ProyectoAbierto.Nombre).ClickAsync(new MouseEventArgs());
        await BotonConTexto(cut, "[role=tab]", "Técnicos").ClickAsync(new MouseEventArgs());

        await cut.Find(".baja-tecnico-proyecto").ClickAsync(new MouseEventArgs());
        cut.FindAll("[role=dialog]").Should().ContainSingle("control positivo: el diálogo se abrió");

        await BotonConTexto(cut, "[role=dialog] .modal-pie button", "Cancelar").ClickAsync(new MouseEventArgs());

        _mediator.Enviados.OfType<DesasignarTecnicoProyectoCommand>().Should().BeEmpty();
        cut.FindAll("[role=dialog]").Should().BeEmpty();
        cut.FindAll(".baja-tecnico-proyecto").Should().ContainSingle("el técnico sigue activo");
    }

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

        _mediator.Enviados.OfType<DesasignarTecnicoProyectoCommand>().Should().BeEmpty("la baja se confirma antes de enviarse");
        cut.Find("[role=dialog]").TextContent.Should().Contain("Salas Moreno, Javier").And.Contain("no se puede deshacer");

        await BotonConTexto(cut, "[role=dialog] .modal-pie button", "Dar de baja").ClickAsync(new MouseEventArgs());

        _mediator.Enviados.OfType<DesasignarTecnicoProyectoCommand>().Should().ContainSingle()
            .Which.Id.Should().Be(TecnicoActivoId);
        cut.FindAll("[role=dialog]").Should().BeEmpty("confirmar cierra el diálogo");

        // El doble devuelve la lista con la baja aplicada: la pantalla tiene que recargarla.
        cut.FindAll(".baja-tecnico-proyecto").Should().BeEmpty("tras la baja se recargan los técnicos y ya no queda ninguno activo");
        cut.FindAll(".fila-tecnico-proyecto").Single(f => f.TextContent.Contains("Salas Moreno, Javier"))
            .TextContent.Should().Contain("De baja").And.Contain("baja 11/09/2026");
    }

    /// <summary>
    /// Decisión del 2026-09-24 (DNI residual, S2): la línea secundaria del técnico lleva solo
    /// las fechas, sin DNI. La igualdad exacta es la que pone esto en rojo si el DNI vuelve.
    /// </summary>
    [Fact]
    public async Task La_linea_del_tecnico_lleva_solo_las_fechas_sin_DNI()
    {
        _mediator.Proyectos = [ProyectoAbierto];
        var cut = await RenderizarConClienteAsync();
        await BotonConTexto(cut, "tbody .nombre-proyecto", ProyectoAbierto.Nombre).ClickAsync(new MouseEventArgs());

        await BotonConTexto(cut, "[role=tab]", "Técnicos").ClickAsync(new MouseEventArgs());

        cut.FindAll(".meta-tecnico-proyecto").Select(m => m.TextContent.Trim()).Should().Equal(
            "alta 02/06/2026",
            "alta 09/06/2026 · baja 31/07/2026");
    }

    [Fact]
    public async Task Consulta_abre_el_panel_sin_que_se_le_ofrezca_editar_cerrar_ni_gestionar_tecnicos()
    {
        // Editar, cerrar, asignar y dar de baja técnicos son ICommand que
        // AutorizacionEscrituraBehavior deniega a Consulta: ofrecerlos era enseñar
        // botones que siempre fallan.
        this.ConRolDeEscritura(Roles.Consulta);
        _mediator.Proyectos = [ProyectoAbierto];
        var cut = await RenderizarConClienteAsync();
        await AbrirDetalle(cut, ProyectoAbierto);

        var botones = cut.FindAll("aside.panel-proyecto button").Select(b => b.TextContent.Trim()).ToList();
        botones.Should().NotContain(["Editar", "Cerrar proyecto"])
            .And.Contain(t => t.StartsWith("Técnicos"), "las pestañas de lectura sí se ofrecen");
        cut.FindAll(".pie-panel-proyecto").Should().BeEmpty("sin acciones no queda un pie vacío");

        await BotonConTexto(cut, "[role=tab]", "Técnicos").ClickAsync(new MouseEventArgs());

        cut.FindAll(".fila-tecnico-proyecto").Should().HaveCount(2, "los técnicos se siguen viendo");
        cut.FindAll("aside.panel-proyecto button").Select(b => b.TextContent.Trim())
            .Should().NotContain(["Dar de baja", "+ Asignar técnico"]);
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
            new TecnicoProyectoDto(Guid.NewGuid(), Guid.NewGuid(), "Iglesias Ruiz, Marta",
                new DateOnly(2026, 7, 1), null, EstaActivo: true),
        ];
        var cut = await RenderizarConClienteAsync();
        await AbrirDetalle(cut, ProyectoAbierto);
        var tecnicosA = _mediator.Retener(r => r is ObtenerTecnicosProyectoQuery q && q.ProyectoId == AbiertoId);

        var pestanaA = BotonConTexto(cut, "[role=tab]", "Técnicos").ClickAsync(new MouseEventArgs());
        await AbrirDetalle(cut, ProyectoAbierto2);
        await Resolver(cut, tecnicosA, (IReadOnlyList<TecnicoProyectoDto>)
        [
            new TecnicoProyectoDto(TecnicoActivoId, Guid.NewGuid(), "Salas Moreno, Javier",
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

    // ------------------------------------------------------------------ atajos de lista (I-8)

    private static Task Atajo(IRenderedComponent<Proyectos> cut, string tecla) =>
        cut.InvokeAsync(() => cut.FindComponent<AtajosListaTeclado>().Instance.RecibirAtajo(tecla));

    private static List<string> FilasEnfocadas(IRenderedComponent<Proyectos> cut) =>
        cut.FindAll("tbody tr.fila-enfocada")
            .Select(f => f.QuerySelector(".nombre-proyecto")!.TextContent.Trim())
            .ToList();

    /// <summary>
    /// I-8: Proyectos nunca tuvo <c>AtajosListaTeclado</c> —no es una regresión
    /// de Gen2, es un hueco que no se cerró—. "j" y "k" recorren las filas
    /// visibles sin abrir nada: el foco de teclado es distinto de la selección
    /// del panel, y el panel sigue cerrado mientras solo se navega.
    /// </summary>
    [Fact]
    public async Task j_y_k_recorren_las_filas_sin_abrir_el_panel()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoAbierto2, ProyectoCerrado];
        var cut = await RenderizarConClienteAsync();

        FilasEnfocadas(cut).Should().BeEmpty("sin pulsar nada no hay fila enfocada");

        await Atajo(cut, "j");
        FilasEnfocadas(cut).Should().Equal([ProyectoAbierto.Nombre]);

        await Atajo(cut, "j");
        FilasEnfocadas(cut).Should().Equal([ProyectoAbierto2.Nombre]);

        await Atajo(cut, "k");
        FilasEnfocadas(cut).Should().Equal([ProyectoAbierto.Nombre]);

        cut.FindAll("aside.panel-proyecto").Should().BeEmpty("recorrer no abre el detalle");
        _mediator.Enviados.OfType<ObtenerProyectoPorIdQuery>().Should().BeEmpty();
    }

    /// <summary>"j" en la última fila y "k" en la primera se quedan donde están.</summary>
    [Fact]
    public async Task j_y_k_no_se_salen_de_la_lista()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoAbierto2];
        var cut = await RenderizarConClienteAsync();

        await Atajo(cut, "k");
        FilasEnfocadas(cut).Should().Equal([ProyectoAbierto.Nombre], "sin foco previo, \"k\" empieza por la primera");

        await Atajo(cut, "k");
        FilasEnfocadas(cut).Should().Equal([ProyectoAbierto.Nombre]);

        await Atajo(cut, "j");
        await Atajo(cut, "j");
        await Atajo(cut, "j");
        FilasEnfocadas(cut).Should().Equal([ProyectoAbierto2.Nombre]);
    }

    /// <summary>
    /// El caso que el contrato (§ 6.1 quater) documenta como bug histórico de
    /// esta misma pantalla: "Enter" debe abrir el proyecto enfocado, no seguir
    /// el enlace de la celda "Centro". Se comprueba con cambio de estado —qué
    /// proyecto pide el panel— y no con una ausencia de navegación.
    /// </summary>
    [Fact]
    public async Task Enter_abre_el_proyecto_enfocado_y_no_el_primero_de_la_lista()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoAbierto2, ProyectoCerrado];
        var cut = await RenderizarConClienteAsync();

        await Atajo(cut, "j");
        await Atajo(cut, "j");
        await Atajo(cut, "j");
        await Atajo(cut, "Enter");

        cut.Find("aside.panel-proyecto").TextContent.Should().Contain(ProyectoCerrado.Nombre);
        _mediator.Enviados.OfType<ObtenerProyectoPorIdQuery>().Should().ContainSingle()
            .Which.Id.Should().Be(CerradoId);
    }

    /// <summary>Sin fila enfocada, "Enter" no abre nada: no hay "el primero por defecto".</summary>
    [Fact]
    public async Task Enter_sin_fila_enfocada_no_abre_nada()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoAbierto2];
        var cut = await RenderizarConClienteAsync();

        await Atajo(cut, "Enter");

        cut.FindAll("aside.panel-proyecto").Should().BeEmpty();
        _mediator.Enviados.OfType<ObtenerProyectoPorIdQuery>().Should().BeEmpty();
    }

    /// <summary>
    /// "x" no marca nada y es deliberado: Proyectos no tiene selección múltiple
    /// —ni casillas ni barra de acciones de lote—, así que no hay nada que
    /// marcar y darle significado sería una decisión de producto (el mismo
    /// caso que I-12 en Estado Comercial). Lo que sí se exige es que no mueva
    /// el foco ni abra el panel.
    /// </summary>
    [Fact]
    public async Task x_no_tiene_efecto_porque_la_pantalla_no_tiene_seleccion_multiple()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoAbierto2];
        var cut = await RenderizarConClienteAsync();
        await Atajo(cut, "j");

        await Atajo(cut, "x");

        FilasEnfocadas(cut).Should().Equal([ProyectoAbierto.Nombre]);
        cut.FindAll("tbody input[type=checkbox]").Should().BeEmpty();
        cut.FindAll("aside.panel-proyecto").Should().BeEmpty();
    }

    /// <summary>
    /// Guarda P3-31, mitad observable desde bUnit: escribir en el buscador no
    /// mueve el foco de fila. La otra mitad —que con un campo de texto
    /// enfocado la tecla ni siquiera llega a C#— la decide
    /// <c>atajos-lista.js</c> y se prueba donde se puede observar de verdad,
    /// con teclado real, en <c>AtajosSuperficiesTests</c>
    /// (<c>La_edicion_y_los_selectores_conservan_sus_teclas</c>): este test no
    /// la demuestra y no pretende sustituirla.
    /// </summary>
    [Fact]
    public async Task Escribir_en_el_buscador_no_mueve_el_foco_de_fila()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoAbierto2];
        var cut = await RenderizarConClienteAsync();
        await Atajo(cut, "j");

        // "a" mantiene visibles las dos filas: si el término las escondiera, un
        // foco perdido y un foco movido serían indistinguibles.
        await cut.Find("input[data-filtro-pantalla]")
            .InputAsync(new ChangeEventArgs { Value = "a" });

        NombresEnLaTabla(cut).Should().HaveCount(2);
        FilasEnfocadas(cut).Should().Equal([ProyectoAbierto.Nombre]);
    }

    /// <summary>
    /// Si la fila enfocada deja de pasar los filtros, el foco se descarta: la
    /// siguiente "j" empieza por la primera fila visible y, sobre todo,
    /// quitar el filtro NO devuelve el foco a la fila de antes — hallazgo de
    /// la revisión de Codex sobre este mismo incremento, donde el foco
    /// escondido sobrevivía y reaparecía al levantar el filtro.
    /// </summary>
    [Fact]
    public async Task El_foco_de_una_fila_que_el_filtro_esconde_no_sobrevive_ni_reaparece()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoCerrado];
        var cut = await RenderizarConClienteAsync();
        await Atajo(cut, "j");
        await Atajo(cut, "j");
        FilasEnfocadas(cut).Should().Equal([ProyectoCerrado.Nombre]);

        await ElegirPastillaAsync(cut, "Estado", "Abiertos");

        FilasEnfocadas(cut).Should().BeEmpty();

        await ElegirPastillaAsync(cut, "Estado", "Todos");

        NombresEnLaTabla(cut).Should().HaveCount(2, "el filtro ya no esconde nada");
        FilasEnfocadas(cut).Should().BeEmpty("el foco se descartó al esconderse su fila, no se guardó");

        await Atajo(cut, "j");
        FilasEnfocadas(cut).Should().Equal([ProyectoAbierto.Nombre]);
    }



    // ------------------------------------------------------------------ P1-E2b: aviso de cambios sin guardar

    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    private async Task<IRenderedComponent<Proyectos>> AbrirNuevoProyectoAsync()
    {
        _mediator.CentrosClienteA = [CentroDeA];
        var cut = await RenderizarConClienteAsync();
        await cut.FindAll("button").First(b => b.TextContent.Trim() == "+ Nuevo proyecto").ClickAsync(new MouseEventArgs());
        cut.FindAll(".drawer-panel").Should().NotBeEmpty("el test necesita el drawer abierto");
        return cut;
    }

    private static Task EscribirNombreDelProyectoAsync(IRenderedComponent<Proyectos> cut, string nombre) =>
        cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Nombre del proyecto")
            .Find("input").InputAsync(new ChangeEventArgs { Value = nombre });

    [Fact]
    public async Task Aviso_nuevo_proyecto_con_la_fecha_de_hoy_puesta_no_pregunta()
    {
        var cut = await AbrirNuevoProyectoAsync();
        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "la fecha de inicio de hoy viene puesta: no es un cambio");
    }

    /// <summary>D-05: «Cancelar» del alta de Proyecto cierra como la X.</summary>
    [Fact]
    public async Task Cancelar_el_nuevo_proyecto_con_nombre_pregunta_y_sin_tocar_cierra()
    {
        var cut = await AbrirNuevoProyectoAsync();
        await cut.ComprobarQueCancelarSinCambiosCierraAsync(".drawer-pie", ".drawer-panel");

        await cut.FindAll("button").First(b => b.TextContent.Trim() == "+ Nuevo proyecto").ClickAsync(new MouseEventArgs());
        await EscribirNombreDelProyectoAsync(cut, "Montaje cámaras 2026");
        await cut.PulsarCancelarDelPieAsync(".drawer-pie");

        await cut.ComprobarQuePreguntaYDescartarAsync(".drawer-panel");
    }

    [Fact]
    public async Task Aviso_nuevo_proyecto_con_nombre_escrito_pregunta_al_salir()
    {
        var cut = await AbrirNuevoProyectoAsync();

        await EscribirNombreDelProyectoAsync(cut, "Montaje cámaras 2026");

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task Aviso_crear_el_proyecto_deja_salir_sin_preguntar()
    {
        var cut = await AbrirNuevoProyectoAsync();
        await cut.Find(".drawer-panel select").ChangeAsync(new ChangeEventArgs { Value = CentroDeA.Id.ToString() });
        await EscribirNombreDelProyectoAsync(cut, "Montaje cámaras 2026");

        await BotonConTexto(cut, ".drawer-panel button", "Crear proyecto").ClickAsync(new MouseEventArgs());

        _mediator.Enviados.OfType<CrearProyectoCommand>().Should().ContainSingle("el caso solo vale si se creó");
        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "lo escrito ya está guardado");
    }

    [Fact]
    public async Task Aviso_cerrar_proyecto_con_la_fecha_de_hoy_puesta_no_pregunta()
    {
        _mediator.Proyectos = [ProyectoAbierto];
        var cut = await RenderizarConClienteAsync();
        await CerrarDesdeLaFilaAsync(cut, ProyectoAbierto);
        cut.FindAll("[role=dialog]").Should().NotBeEmpty("el test necesita el modal abierto");

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "la fecha de cierre de hoy viene puesta: no es un cambio");
    }

    [Fact]
    public async Task Aviso_cerrar_proyecto_con_otra_fecha_pregunta_al_salir()
    {
        _mediator.Proyectos = [ProyectoAbierto];
        var cut = await RenderizarConClienteAsync();
        await CerrarDesdeLaFilaAsync(cut, ProyectoAbierto);
        await cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Fecha de cierre")
            .Find("input").InputAsync(new ChangeEventArgs { Value = "2020-01-01" });

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task Aviso_confirmar_el_cierre_con_otra_fecha_deja_salir_sin_preguntar()
    {
        _mediator.Proyectos = [ProyectoAbierto];
        var cut = await RenderizarConClienteAsync();
        await CerrarDesdeLaFilaAsync(cut, ProyectoAbierto);
        await cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Fecha de cierre")
            .Find("input").InputAsync(new ChangeEventArgs { Value = "2020-01-01" });

        await ConfirmarCierre(cut);

        _mediator.Enviados.OfType<CerrarProyectoCommand>().Should().ContainSingle("el caso solo vale si se cerró");
        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "el cierre ya está guardado");
    }

    [Fact]
    public async Task Cerrar_proyecto_con_la_fecha_vacia_avisa_en_el_aviso_fijo_del_modal_sin_enviar_y_el_aviso_se_va_al_escribir()
    {
        _mediator.Proyectos = [ProyectoAbierto];
        var cut = await RenderizarConClienteAsync();
        await CerrarDesdeLaFilaAsync(cut, ProyectoAbierto);
        var campo = cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Fecha de cierre");
        cut.Find("[role=dialog] .modal-cuerpo").QuerySelectorAll("[role=alert]").Should().BeEmpty("control positivo: sin intento no hay aviso");

        await campo.Find("input").InputAsync(new ChangeEventArgs { Value = "" });
        await ConfirmarCierre(cut);

        _mediator.Enviados.OfType<CerrarProyectoCommand>().Should().BeEmpty("la fecha no es válida: no se envía");
        cut.Find("[role=dialog] [role=alert]").TextContent.Trim().Should().Be("Introduce una fecha de cierre válida.");
        cut.FindAll("[role=dialog] .modal-cuerpo [role=alert]").Should().BeEmpty("el aviso va fuera del cuerpo desplazable (D-20)");

        await cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Fecha de cierre")
            .Find("input").InputAsync(new ChangeEventArgs { Value = "2020-01-01" });
        cut.FindAll("[role=dialog] [role=alert]").Should().BeEmpty("el aviso era del intento anterior");
    }

    // ------------------------------------------------------------------ P1-E2b lote 2: panel de detalle

    private static bool PreguntaAbierta(IRenderedComponent<Proyectos> cut) =>
        cut.FindAll(".modal-pie button").Any(b => b.TextContent.Trim() == "Salir y descartar");

    private static bool PanelDeDetalleAbierto(IRenderedComponent<Proyectos> cut) => cut.FindAll("aside.panel-proyecto").Count > 0;

    private static Task PulsarEnLaPreguntaAsync(IRenderedComponent<Proyectos> cut, string texto) =>
        cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == texto).ClickAsync(new MouseEventArgs());

    /// <summary>
    /// Sin cambios el gesto termina solo y no pregunta. Se espera la tarea con un tope corto: si el aviso preguntara
    /// (y nadie contestara), la tarea no termina nunca, y esperarla sin tope colgaría el test en vez de ponerlo en rojo.
    /// </summary>
    private static async Task ComprobarQueTerminaSinPreguntarAsync(IRenderedComponent<Proyectos> cut, Task gesto, string porque)
    {
        await Task.WhenAny(gesto, Task.Delay(TimeSpan.FromSeconds(2)));
        PreguntaAbierta(cut).Should().BeFalse(porque);
        gesto.IsCompleted.Should().BeTrue("sin cambios el gesto no se queda esperando la respuesta de una pregunta: " + porque);
        await gesto;
    }

    private static Task CerrarElPanelAsync(IRenderedComponent<Proyectos> cut) =>
        cut.Find(".cerrar-panel-proyecto").ClickAsync(new MouseEventArgs());

    private static IElement CampoDelPanel(IRenderedComponent<Proyectos> cut, string etiqueta) =>
        cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == etiqueta).Find("input");

    /// <summary>
    /// Lo que el formulario del panel tiene ahora (el <c>Valor</c> que la página pasa al campo). El atributo
    /// <c>value</c> del <c>&lt;input&gt;</c> no sirve: CampoTexto no lo actualiza al teclear.
    /// </summary>
    private static string ValorDelCampo(IRenderedComponent<Proyectos> cut, string etiqueta) =>
        cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == etiqueta).Instance.Valor;

    private async Task<IRenderedComponent<Proyectos>> AbrirLaEdicionDelDetalleAsync()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoAbierto2];
        var cut = await RenderizarConClienteAsync();
        await AbrirDetalle(cut, ProyectoAbierto);
        await BotonConTexto(cut, ".pie-panel-proyecto button", "Editar").ClickAsync(new MouseEventArgs());
        ValorDelCampo(cut, "Nombre").Should().Be(ProyectoAbierto.Nombre, "el test necesita la edición abierta");
        return cut;
    }

    private static Task EscribirEnElPanelAsync(IRenderedComponent<Proyectos> cut, string valor) =>
        CampoDelPanel(cut, "Nombre").InputAsync(new ChangeEventArgs { Value = valor });

    private async Task<IRenderedComponent<Proyectos>> AbrirElAltaDeTecnicoAsync()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoAbierto2];
        var cut = await RenderizarConClienteAsync();
        await AbrirDetalle(cut, ProyectoAbierto);
        await cut.FindAll("button[role=tab]").Single(b => b.TextContent.Trim() == "Técnicos").ClickAsync(new MouseEventArgs());
        await BotonConTexto(cut, ".acciones-seccion button", "+ Asignar técnico").ClickAsync(new MouseEventArgs());
        cut.FindComponents<CampoTexto>().Should().Contain(c => c.Instance.Etiqueta == "Fecha de alta", "el test necesita el alta de técnico abierta");
        return cut;
    }

    private static Task CambiarLaFechaDeAltaAsync(IRenderedComponent<Proyectos> cut) =>
        CampoDelPanel(cut, "Fecha de alta").InputAsync(new ChangeEventArgs { Value = "2020-01-01" });

    [Fact]
    public async Task Aviso_edicion_de_informacion_abierta_sin_tocar_nada_no_pregunta_al_cerrar_el_panel()
    {
        var cut = await AbrirLaEdicionDelDetalleAsync();

        await ComprobarQueTerminaSinPreguntarAsync(cut, CerrarElPanelAsync(cut), "no hay nada escrito");

        PanelDeDetalleAbierto(cut).Should().BeFalse("cerrar sin cambios cierra el panel");
    }

    [Fact]
    public async Task Aviso_edicion_de_informacion_abierta_sin_tocar_nada_no_pregunta_al_salir()
    {
        var cut = await AbrirLaEdicionDelDetalleAsync();

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "abrir la edición no cambia nada");
    }

    [Fact]
    public async Task Aviso_edicion_de_informacion_con_cambios_pregunta_al_salir_de_la_pantalla()
    {
        var cut = await AbrirLaEdicionDelDetalleAsync();

        await EscribirEnElPanelAsync(cut, "Otro nombre");

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task Aviso_cerrar_el_panel_con_la_edicion_a_medias_pregunta_seguir_editando_conserva_y_descartar_cierra()
    {
        var cut = await AbrirLaEdicionDelDetalleAsync();
        await EscribirEnElPanelAsync(cut, "Otro nombre");

        // Sin await: la X queda pendiente de la respuesta del aviso; se afirma antes de esperarla.
        var cierre = CerrarElPanelAsync(cut);
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue("la X iba a tirar lo escrito"));
        PanelDeDetalleAbierto(cut).Should().BeTrue("mientras pregunta, el panel sigue ahí");

        await PulsarEnLaPreguntaAsync(cut, "Seguir editando");
        await cierre.WaitAsync(Paciencia);

        PanelDeDetalleAbierto(cut).Should().BeTrue("«Seguir editando» conserva el panel");
        SelectorDeCliente(cut).GetAttribute("aria-label").Should().Be("Cliente empresarial: Refrielectric S.L.",
            "cancelar conserva también la selección visible de la pastilla");
        ValorDelCampo(cut, "Nombre").Should().Be("Otro nombre", "y conserva lo escrito");

        var segundoCierre = CerrarElPanelAsync(cut);
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue());
        await PulsarEnLaPreguntaAsync(cut, "Salir y descartar");
        await segundoCierre.WaitAsync(Paciencia);

        PanelDeDetalleAbierto(cut).Should().BeFalse("«Salir y descartar» cierra el panel");
    }

    [Fact]
    public async Task Aviso_abrir_otro_proyecto_con_la_edicion_a_medias_pregunta_y_descartar_abre_el_otro()
    {
        var cut = await AbrirLaEdicionDelDetalleAsync();
        await EscribirEnElPanelAsync(cut, "Otro nombre");

        var apertura = AbrirDetalle(cut, ProyectoAbierto2);
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue("abrir otro proyecto tira lo escrito"));
        _mediator.Enviados.OfType<ObtenerProyectoPorIdQuery>().Should().OnlyContain(q => q.Id == AbiertoId,
            "no se pide el otro proyecto mientras pregunta");

        await PulsarEnLaPreguntaAsync(cut, "Seguir editando");
        await apertura.WaitAsync(Paciencia);
        ValorDelCampo(cut, "Nombre").Should().Be("Otro nombre", "«Seguir editando» conserva la edición");

        var segunda = AbrirDetalle(cut, ProyectoAbierto2);
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue());
        await PulsarEnLaPreguntaAsync(cut, "Salir y descartar");
        await segunda.WaitAsync(Paciencia);

        cut.Find(".nombre-cabecera-panel-proyecto").TextContent.Should().Be(ProyectoAbierto2.Nombre, "descartar abre el otro proyecto");
    }

    [Fact]
    public async Task Aviso_cambiar_de_Cliente_empresarial_con_la_edicion_a_medias_pregunta_seguir_conserva_y_descartar_cambia()
    {
        var cut = await AbrirLaEdicionDelDetalleAsync();
        await EscribirEnElPanelAsync(cut, "Otro nombre");
        var consultasDeProyectosAntes = _mediator.Enviados.OfType<ObtenerProyectosQuery>().Count();

        var cambio = ElegirCliente(cut, ClienteBId);
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue("cambiar de Cliente empresarial cierra el panel y tira lo escrito"));
        _mediator.Enviados.OfType<ObtenerProyectosQuery>().Count().Should().Be(consultasDeProyectosAntes,
            "no se cambia de Cliente empresarial mientras pregunta");

        await PulsarEnLaPreguntaAsync(cut, "Seguir editando");
        await cambio.WaitAsync(Paciencia);

        PanelDeDetalleAbierto(cut).Should().BeTrue("«Seguir editando» conserva el panel");
        SelectorDeCliente(cut).GetAttribute("aria-label").Should().Be("Cliente empresarial: Refrielectric S.L.",
            "cancelar conserva también la selección visible de la pastilla");
        ValorDelCampo(cut, "Nombre").Should().Be("Otro nombre");
        _mediator.Enviados.OfType<ObtenerProyectosQuery>().Count().Should().Be(consultasDeProyectosAntes,
            "seguir editando no recarga la lista del otro Cliente empresarial");

        var segundo = ElegirCliente(cut, ClienteBId);
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue());
        await PulsarEnLaPreguntaAsync(cut, "Salir y descartar");
        await segundo.WaitAsync(Paciencia);

        PanelDeDetalleAbierto(cut).Should().BeFalse("descartar cambia de Cliente empresarial y cierra el panel");
        _mediator.Enviados.OfType<ObtenerProyectosQuery>().Last().ClienteId.Should().Be(ClienteBId);
    }

    [Fact]
    public async Task Aviso_cambiar_de_Cliente_empresarial_con_la_edicion_sin_tocar_no_pregunta()
    {
        var cut = await AbrirLaEdicionDelDetalleAsync();

        await ComprobarQueTerminaSinPreguntarAsync(cut, ElegirCliente(cut, ClienteBId), "la edición está como se abrió");

        _mediator.Enviados.OfType<ObtenerProyectosQuery>().Last().ClienteId.Should().Be(ClienteBId);
    }

    [Fact]
    public async Task Aviso_alta_de_tecnico_abierta_sin_tocar_nada_no_pregunta_al_cerrar_el_panel()
    {
        var cut = await AbrirElAltaDeTecnicoAsync();

        await ComprobarQueTerminaSinPreguntarAsync(cut, CerrarElPanelAsync(cut), "la fecha de alta de hoy viene puesta: no es un cambio");

        PanelDeDetalleAbierto(cut).Should().BeFalse();
    }

    [Fact]
    public async Task Aviso_alta_de_tecnico_abierta_sin_tocar_nada_no_pregunta_al_salir()
    {
        var cut = await AbrirElAltaDeTecnicoAsync();

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "la fecha de alta de hoy viene puesta: no es un cambio");
    }

    [Fact]
    public async Task Aviso_alta_de_tecnico_con_cambios_pregunta_al_salir()
    {
        var cut = await AbrirElAltaDeTecnicoAsync();

        await CambiarLaFechaDeAltaAsync(cut);

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task Aviso_alta_de_tecnico_con_cambios_al_cerrar_el_panel_pregunta_seguir_conserva_y_descartar_cierra()
    {
        var cut = await AbrirElAltaDeTecnicoAsync();
        await CambiarLaFechaDeAltaAsync(cut);

        var cierre = CerrarElPanelAsync(cut);
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue("cerrar el panel tiraba el alta a medias"));
        await PulsarEnLaPreguntaAsync(cut, "Seguir editando");
        await cierre.WaitAsync(Paciencia);
        PanelDeDetalleAbierto(cut).Should().BeTrue("«Seguir editando» conserva el panel");
        SelectorDeCliente(cut).GetAttribute("aria-label").Should().Be("Cliente empresarial: Refrielectric S.L.",
            "cancelar conserva también la selección visible de la pastilla");
        ValorDelCampo(cut, "Fecha de alta").Should().Be("2020-01-01");

        var segundo = CerrarElPanelAsync(cut);
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue());
        await PulsarEnLaPreguntaAsync(cut, "Salir y descartar");
        await segundo.WaitAsync(Paciencia);

        PanelDeDetalleAbierto(cut).Should().BeFalse();
    }

    [Fact]
    public async Task Aviso_el_drawer_de_nuevo_proyecto_pregunta_solo_por_su_contenido_no_por_la_edicion_del_panel()
    {
        var cut = await AbrirLaEdicionDelDetalleAsync();
        await EscribirEnElPanelAsync(cut, "Otro nombre");
        _mediator.CentrosClienteA = [CentroDeA];
        await cut.FindAll("button").First(b => b.TextContent.Trim() == "+ Nuevo proyecto").ClickAsync(new MouseEventArgs());
        cut.FindAll(".drawer-panel").Should().NotBeEmpty("el test necesita el drawer abierto");

        await cut.Find(".drawer-cerrar").ClickAsync(new MouseEventArgs());

        cut.FindAll(".drawer-panel").Should().BeEmpty("el drawer no tiene nada escrito: la edición del panel no es suya");
        ValorDelCampo(cut, "Nombre").Should().Be("Otro nombre", "y la edición del panel sigue intacta");
    }

    // ------------------------------------------------------------------ P1-E2b lote 2: revisión estática independiente

    /// <summary>Abre el menú de la fila del proyecto y pulsa una de sus acciones (sin esperar a lo que ella dispare).</summary>
    private static async Task<Task> PulsarAccionDeLaFilaAsync(IRenderedComponent<Proyectos> cut, ProyectoListaDto proyecto, string accion)
    {
        var fila = cut.FindAll("tbody tr").Single(f => f.QuerySelector(".nombre-proyecto")!.TextContent.Trim() == proyecto.Nombre);
        await fila.QuerySelector(".menu-acciones-disparador")!.ClickAsync(new MouseEventArgs());
        return BotonConTexto(cut, "[role=menuitem]", accion).ClickAsync(new MouseEventArgs());
    }

    /// <summary>Elimina desde el menú de la fila y confirma en el diálogo (su botón de confirmar dice «Eliminar»).</summary>
    private async Task EliminarDesdeLaFilaAsync(IRenderedComponent<Proyectos> cut, ProyectoListaDto proyecto, bool descartandoLoEscrito = false)
    {
        var accion = await PulsarAccionDeLaFilaAsync(cut, proyecto, "Eliminar");
        if (descartandoLoEscrito)
        {
            // Eliminar el proyecto abierto con algo a medias pregunta primero (M2); aquí se responde «descartar».
            cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue("eliminar el proyecto abierto tira lo escrito"));
            await PulsarEnLaPreguntaAsync(cut, "Salir y descartar");
        }
        await accion.WaitAsync(Paciencia);
        await BotonConTexto(cut, "[role=dialog] .modal-pie button", "Eliminar").ClickAsync(new MouseEventArgs());
        _mediator.Enviados.OfType<EliminarProyectoCommand>().Should().ContainSingle("el caso solo vale si se eliminó")
            .Which.Id.Should().Be(proyecto.Id);
        PanelDeDetalleAbierto(cut).Should().BeFalse("eliminar el proyecto abierto cierra su panel");
    }

    // ---- Eliminar el proyecto cuyo detalle está abierto no deja un aviso fantasma (defecto de este diff, corregido en f440cea4)

    [Fact]
    public async Task Aviso_eliminar_el_proyecto_abierto_con_el_alta_de_tecnico_a_medias_no_deja_un_aviso_al_salir()
    {
        var cut = await AbrirElAltaDeTecnicoAsync();
        await CambiarLaFechaDeAltaAsync(cut);

        await EliminarDesdeLaFilaAsync(cut, ProyectoAbierto, descartandoLoEscrito: true);

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "el proyecto con el alta a medias ya no existe: no queda nada que perder");
    }

    [Fact]
    public async Task Aviso_eliminar_el_proyecto_abierto_con_el_alta_de_tecnico_a_medias_no_pregunta_al_abrir_otro()
    {
        var cut = await AbrirElAltaDeTecnicoAsync();
        await CambiarLaFechaDeAltaAsync(cut);

        await EliminarDesdeLaFilaAsync(cut, ProyectoAbierto, descartandoLoEscrito: true);

        await ComprobarQueTerminaSinPreguntarAsync(cut, AbrirDetalle(cut, ProyectoAbierto2), "el alta a medias era del proyecto eliminado");
        cut.Find(".nombre-cabecera-panel-proyecto").TextContent.Should().Be(ProyectoAbierto2.Nombre);
    }

    // ---- Enter y «Detalles» del menú de la fila con la edición de información sucia (ManejarAtajoAsync y menú → AbrirDetalleConAvisoAsync)

    [Fact]
    public async Task Aviso_Enter_sobre_otra_fila_con_la_edicion_a_medias_pregunta_seguir_conserva_y_descartar_abre_el_otro()
    {
        var cut = await AbrirLaEdicionDelDetalleAsync();
        await EscribirEnElPanelAsync(cut, "Otro nombre");
        await Atajo(cut, "j");
        await Atajo(cut, "j");
        FilasEnfocadas(cut).Should().Equal([ProyectoAbierto2.Nombre], "el test necesita el foco en la otra fila");

        var apertura = Atajo(cut, "Enter");
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue("Enter sobre otra fila tira lo escrito"));
        await PulsarEnLaPreguntaAsync(cut, "Seguir editando");
        await apertura.WaitAsync(Paciencia);
        ValorDelCampo(cut, "Nombre").Should().Be("Otro nombre", "«Seguir editando» conserva la edición");

        var segunda = Atajo(cut, "Enter");
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue());
        await PulsarEnLaPreguntaAsync(cut, "Salir y descartar");
        await segunda.WaitAsync(Paciencia);

        cut.Find(".nombre-cabecera-panel-proyecto").TextContent.Should().Be(ProyectoAbierto2.Nombre, "descartar abre el otro proyecto");
    }

    [Fact]
    public async Task Aviso_Enter_con_la_edicion_sin_tocar_no_pregunta()
    {
        var cut = await AbrirLaEdicionDelDetalleAsync();
        await Atajo(cut, "j");
        await Atajo(cut, "j");

        await ComprobarQueTerminaSinPreguntarAsync(cut, Atajo(cut, "Enter"), "la edición está como se abrió");

        cut.Find(".nombre-cabecera-panel-proyecto").TextContent.Should().Be(ProyectoAbierto2.Nombre);
    }

    [Fact]
    public async Task Aviso_menu_Detalles_de_otra_fila_con_la_edicion_a_medias_pregunta_seguir_conserva_y_descartar_abre_el_otro()
    {
        var cut = await AbrirLaEdicionDelDetalleAsync();
        await EscribirEnElPanelAsync(cut, "Otro nombre");

        var apertura = await PulsarAccionDeLaFilaAsync(cut, ProyectoAbierto2, "Detalles");
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue("«Detalles» de otra fila tira lo escrito"));
        await PulsarEnLaPreguntaAsync(cut, "Seguir editando");
        await apertura.WaitAsync(Paciencia);
        ValorDelCampo(cut, "Nombre").Should().Be("Otro nombre", "«Seguir editando» conserva la edición");

        var segunda = await PulsarAccionDeLaFilaAsync(cut, ProyectoAbierto2, "Detalles");
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue());
        await PulsarEnLaPreguntaAsync(cut, "Salir y descartar");
        await segunda.WaitAsync(Paciencia);

        cut.Find(".nombre-cabecera-panel-proyecto").TextContent.Should().Be(ProyectoAbierto2.Nombre, "descartar abre el otro proyecto");
    }

    [Fact]
    public async Task Aviso_menu_Detalles_con_la_edicion_sin_tocar_no_pregunta()
    {
        var cut = await AbrirLaEdicionDelDetalleAsync();

        var apertura = await PulsarAccionDeLaFilaAsync(cut, ProyectoAbierto2, "Detalles");

        await ComprobarQueTerminaSinPreguntarAsync(cut, apertura, "la edición está como se abrió");
        cut.Find(".nombre-cabecera-panel-proyecto").TextContent.Should().Be(ProyectoAbierto2.Nombre);
    }

    // ---- Alta de técnico sucia: abrir otro proyecto y cambiar de Cliente empresarial

    [Fact]
    public async Task Aviso_abrir_otro_proyecto_con_el_alta_de_tecnico_a_medias_pregunta_seguir_conserva_y_descartar_abre_el_otro()
    {
        var cut = await AbrirElAltaDeTecnicoAsync();
        await CambiarLaFechaDeAltaAsync(cut);

        var apertura = AbrirDetalle(cut, ProyectoAbierto2);
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue("abrir otro proyecto tira el alta a medias"));
        await PulsarEnLaPreguntaAsync(cut, "Seguir editando");
        await apertura.WaitAsync(Paciencia);
        ValorDelCampo(cut, "Fecha de alta").Should().Be("2020-01-01", "«Seguir editando» conserva el alta");

        var segunda = AbrirDetalle(cut, ProyectoAbierto2);
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue());
        await PulsarEnLaPreguntaAsync(cut, "Salir y descartar");
        await segunda.WaitAsync(Paciencia);

        cut.Find(".nombre-cabecera-panel-proyecto").TextContent.Should().Be(ProyectoAbierto2.Nombre, "descartar abre el otro proyecto");
        cut.FindComponents<CampoTexto>().Should().NotContain(c => c.Instance.Etiqueta == "Fecha de alta", "el alta se descartó");
    }

    [Fact]
    public async Task Aviso_cambiar_de_Cliente_empresarial_con_el_alta_de_tecnico_a_medias_pregunta_seguir_conserva_y_descartar_cambia()
    {
        var cut = await AbrirElAltaDeTecnicoAsync();
        await CambiarLaFechaDeAltaAsync(cut);
        var consultasDeProyectosAntes = _mediator.Enviados.OfType<ObtenerProyectosQuery>().Count();

        var cambio = ElegirCliente(cut, ClienteBId);
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue("cambiar de Cliente empresarial tira el alta a medias"));
        _mediator.Enviados.OfType<ObtenerProyectosQuery>().Count().Should().Be(consultasDeProyectosAntes,
            "no se cambia de Cliente empresarial mientras pregunta");

        await PulsarEnLaPreguntaAsync(cut, "Seguir editando");
        await cambio.WaitAsync(Paciencia);
        PanelDeDetalleAbierto(cut).Should().BeTrue("«Seguir editando» conserva el panel");
        SelectorDeCliente(cut).GetAttribute("aria-label").Should().Be("Cliente empresarial: Refrielectric S.L.",
            "cancelar conserva también la selección visible de la pastilla");
        ValorDelCampo(cut, "Fecha de alta").Should().Be("2020-01-01");
        _mediator.Enviados.OfType<ObtenerProyectosQuery>().Count().Should().Be(consultasDeProyectosAntes);

        var segundo = ElegirCliente(cut, ClienteBId);
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue());
        await PulsarEnLaPreguntaAsync(cut, "Salir y descartar");
        await segundo.WaitAsync(Paciencia);

        PanelDeDetalleAbierto(cut).Should().BeFalse("descartar cambia de Cliente empresarial y cierra el panel");
        _mediator.Enviados.OfType<ObtenerProyectosQuery>().Last().ClienteId.Should().Be(ClienteBId);
    }

    // ---- El modal de cierre pregunta solo por lo suyo

    [Fact]
    public async Task Aviso_el_modal_de_cierre_pregunta_solo_por_su_contenido_no_por_la_edicion_del_panel()
    {
        var cut = await AbrirLaEdicionDelDetalleAsync();
        await EscribirEnElPanelAsync(cut, "Otro nombre");
        // Se cierra OTRO proyecto: la edición es del abierto, así que abrir este modal no pierde nada y no pregunta.
        await (await PulsarAccionDeLaFilaAsync(cut, ProyectoAbierto2, "Cerrar proyecto")).WaitAsync(Paciencia);
        cut.FindAll("[role=dialog]").Should().NotBeEmpty("el test necesita el modal de cierre abierto");

        await cut.Find("[role=dialog] .modal-cerrar").ClickAsync(new MouseEventArgs());

        cut.FindAll(".modal-pie button").Should().NotContain(b => b.TextContent.Trim() == "Descartar cambios",
            "el modal de cierre no tiene nada escrito: la edición del panel no es suya");
        cut.FindAll("[role=dialog]").Should().BeEmpty("el modal se cerró sin preguntar");
        ValorDelCampo(cut, "Nombre").Should().Be("Otro nombre", "y la edición del panel sigue intacta");
    }

    [Fact]
    public async Task Aviso_el_modal_de_cierre_con_otra_fecha_pregunta_al_cerrar_con_la_X()
    {
        _mediator.Proyectos = [ProyectoAbierto];
        var cut = await RenderizarConClienteAsync();
        await CerrarDesdeLaFilaAsync(cut, ProyectoAbierto);
        await cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Fecha de cierre")
            .Find("input").InputAsync(new ChangeEventArgs { Value = "2020-01-01" });

        await cut.Find("[role=dialog] .modal-cerrar").ClickAsync(new MouseEventArgs());

        cut.FindAll(".modal-pie button").Should().Contain(b => b.TextContent.Trim() == "Descartar cambios",
            "la fecha de cierre cambiada se perdería al cerrar con la X");
    }

    // ------------------------------------------------------------------ M2 (decidida 2026-09-29): ninguna vía pierde una edición sin preguntar

    private static bool DialogoAbierto(IRenderedComponent<Proyectos> cut) => cut.FindAll("[role=dialog]").Count > 0;

    private static bool HayBoton(IRenderedComponent<Proyectos> cut, string texto) =>
        cut.FindAll("[role=dialog] .modal-pie button").Any(b => b.TextContent.Trim() == texto);

    private async Task<IRenderedComponent<Proyectos>> AbrirLaEdicionDelDetalleCerradoAsync()
    {
        _mediator.Proyectos = [ProyectoCerrado, ProyectoAbierto2];
        var cut = await RenderizarConClienteAsync();
        await AbrirDetalle(cut, ProyectoCerrado);
        await BotonConTexto(cut, ".pie-panel-proyecto button", "Editar").ClickAsync(new MouseEventArgs());
        ValorDelCampo(cut, "Nombre").Should().Be(ProyectoCerrado.Nombre, "el test necesita la edición abierta");
        return cut;
    }

    // ---- Eliminar el proyecto abierto

    [Fact]
    public async Task Aviso_eliminar_el_proyecto_abierto_con_la_edicion_a_medias_pregunta_seguir_conserva_y_descartar_sigue_con_la_eliminacion()
    {
        var cut = await AbrirLaEdicionDelDetalleAsync();
        await EscribirEnElPanelAsync(cut, "Otro nombre");

        // Sin await: la acción queda pendiente de la respuesta del aviso; se afirma antes de esperarla.
        var accion = await PulsarAccionDeLaFilaAsync(cut, ProyectoAbierto, "Eliminar");
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue("eliminar el proyecto abierto tira lo escrito"));
        HayBoton(cut, "Eliminar").Should().BeFalse("la confirmación de eliminar no se abre mientras pregunta");

        await PulsarEnLaPreguntaAsync(cut, "Seguir editando");
        await accion.WaitAsync(Paciencia);

        DialogoAbierto(cut).Should().BeFalse("«Seguir editando» no abre la confirmación de eliminar");
        ValorDelCampo(cut, "Nombre").Should().Be("Otro nombre", "y conserva lo escrito");

        var segunda = await PulsarAccionDeLaFilaAsync(cut, ProyectoAbierto, "Eliminar");
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue());
        await PulsarEnLaPreguntaAsync(cut, "Salir y descartar");
        await segunda.WaitAsync(Paciencia);

        HayBoton(cut, "Eliminar").Should().BeTrue("descartar sigue con la confirmación de eliminar");
        _mediator.Enviados.OfType<EliminarProyectoCommand>().Should().BeEmpty("todavía no se ha confirmado");
    }

    /// <summary>
    /// Listados 5/7 (decisiones D1 y D6, 2026-10-08): Proyectos no tiene selección múltiple, así que
    /// «Eliminar» vive también en el pie del panel; y eliminar deja «Deshacer», que restaura ese Proyecto.
    /// </summary>
    [Fact]
    public async Task Eliminar_desde_el_pie_del_panel_pide_confirmacion_y_ofrece_Deshacer_que_restaura_ese_proyecto()
    {
        _mediator.Proyectos = [ProyectoCerrado, ProyectoAbierto];
        var cut = await RenderizarConClienteAsync();
        await AbrirDetalle(cut, ProyectoAbierto);

        await BotonConTexto(cut, ".pie-panel-proyecto button", "Eliminar").ClickAsync(new MouseEventArgs());

        _mediator.Enviados.OfType<EliminarProyectoCommand>().Should().BeEmpty("abrir el diálogo no borra nada");

        await BotonConTexto(cut, "[role=dialog] .modal-pie button", "Eliminar").ClickAsync(new MouseEventArgs());

        _mediator.Enviados.OfType<EliminarProyectoCommand>().Should().Equal([new EliminarProyectoCommand(ProyectoAbierto.Id)]);
        PanelDeDetalleAbierto(cut).Should().BeFalse("eliminar el proyecto abierto cierra su panel");

        var avisos = Services.GetRequiredService<ToastService>();
        var aviso = avisos.Mensajes.Single(m => m.TextoAccion == "Deshacer");

        await cut.InvokeAsync(() => avisos.EjecutarAccionAsync(aviso.Id));

        _mediator.Enviados.OfType<RestaurarProyectoCommand>().Should().Equal([new RestaurarProyectoCommand(ProyectoAbierto.Id)]);
    }

    [Fact]
    public async Task Aviso_eliminar_el_proyecto_abierto_con_la_edicion_sin_tocar_no_pregunta()
    {
        var cut = await AbrirLaEdicionDelDetalleAsync();

        await ComprobarQueTerminaSinPreguntarAsync(cut, await PulsarAccionDeLaFilaAsync(cut, ProyectoAbierto, "Eliminar"), "no hay nada escrito");

        HayBoton(cut, "Eliminar").Should().BeTrue("la confirmación de eliminar se abre directamente");
    }

    [Fact]
    public async Task Aviso_eliminar_otro_proyecto_con_la_edicion_a_medias_no_pregunta()
    {
        var cut = await AbrirLaEdicionDelDetalleAsync();
        await EscribirEnElPanelAsync(cut, "Otro nombre");

        await ComprobarQueTerminaSinPreguntarAsync(cut, await PulsarAccionDeLaFilaAsync(cut, ProyectoAbierto2, "Eliminar"), "la edición es de otro proyecto");

        HayBoton(cut, "Eliminar").Should().BeTrue();
        ValorDelCampo(cut, "Nombre").Should().Be("Otro nombre");
    }

    // ---- Cerrar el proyecto abierto desde el menú de la fila

    [Fact]
    public async Task Aviso_cerrar_el_proyecto_abierto_con_la_edicion_a_medias_pregunta_seguir_conserva_y_descartar_abre_el_modal_de_cierre()
    {
        var cut = await AbrirLaEdicionDelDetalleAsync();
        await EscribirEnElPanelAsync(cut, "Otro nombre");

        var accion = await PulsarAccionDeLaFilaAsync(cut, ProyectoAbierto, "Cerrar proyecto");
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue("cerrar el proyecto abierto recarga el panel y tira lo escrito"));
        cut.FindComponents<CampoTexto>().Should().NotContain(c => c.Instance.Etiqueta == "Fecha de cierre", "el modal de cierre no se abre mientras pregunta");

        await PulsarEnLaPreguntaAsync(cut, "Seguir editando");
        await accion.WaitAsync(Paciencia);

        DialogoAbierto(cut).Should().BeFalse("«Seguir editando» no abre el modal de cierre");
        ValorDelCampo(cut, "Nombre").Should().Be("Otro nombre", "y conserva lo escrito");

        var segunda = await PulsarAccionDeLaFilaAsync(cut, ProyectoAbierto, "Cerrar proyecto");
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue());
        await PulsarEnLaPreguntaAsync(cut, "Salir y descartar");
        await segunda.WaitAsync(Paciencia);

        cut.FindComponents<CampoTexto>().Should().Contain(c => c.Instance.Etiqueta == "Fecha de cierre", "descartar sigue con el modal de cierre");
    }

    [Fact]
    public async Task Aviso_cerrar_el_proyecto_abierto_con_la_edicion_sin_tocar_no_pregunta()
    {
        var cut = await AbrirLaEdicionDelDetalleAsync();

        await ComprobarQueTerminaSinPreguntarAsync(cut, await PulsarAccionDeLaFilaAsync(cut, ProyectoAbierto, "Cerrar proyecto"), "no hay nada escrito");

        cut.FindComponents<CampoTexto>().Should().Contain(c => c.Instance.Etiqueta == "Fecha de cierre");
    }

    // ---- Reabrir el proyecto abierto en el panel desde el menú de la fila

    [Fact]
    public async Task Aviso_reabrir_el_proyecto_abierto_con_la_edicion_a_medias_pregunta_seguir_conserva_y_descartar_abre_la_confirmacion()
    {
        var cut = await AbrirLaEdicionDelDetalleCerradoAsync();
        await EscribirEnElPanelAsync(cut, "Otro nombre");

        var accion = await PulsarAccionDeLaFilaAsync(cut, ProyectoCerrado, "Reabrir proyecto");
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue("reabrir el proyecto abierto recarga el panel y tira lo escrito"));
        HayBoton(cut, "Reabrir proyecto").Should().BeFalse("la confirmación de reabrir no se abre mientras pregunta");

        await PulsarEnLaPreguntaAsync(cut, "Seguir editando");
        await accion.WaitAsync(Paciencia);

        DialogoAbierto(cut).Should().BeFalse("«Seguir editando» no abre la confirmación de reabrir");
        ValorDelCampo(cut, "Nombre").Should().Be("Otro nombre", "y conserva lo escrito");

        var segunda = await PulsarAccionDeLaFilaAsync(cut, ProyectoCerrado, "Reabrir proyecto");
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue());
        await PulsarEnLaPreguntaAsync(cut, "Salir y descartar");
        await segunda.WaitAsync(Paciencia);

        HayBoton(cut, "Reabrir proyecto").Should().BeTrue("descartar sigue con la confirmación de reabrir");
        _mediator.Enviados.OfType<ReabrirProyectoCommand>().Should().BeEmpty("todavía no se ha confirmado");
    }

    [Fact]
    public async Task Aviso_reabrir_el_proyecto_abierto_con_la_edicion_sin_tocar_no_pregunta()
    {
        var cut = await AbrirLaEdicionDelDetalleCerradoAsync();

        await ComprobarQueTerminaSinPreguntarAsync(cut, await PulsarAccionDeLaFilaAsync(cut, ProyectoCerrado, "Reabrir proyecto"), "no hay nada escrito");

        HayBoton(cut, "Reabrir proyecto").Should().BeTrue("la confirmación de reabrir se abre directamente");
    }
}
