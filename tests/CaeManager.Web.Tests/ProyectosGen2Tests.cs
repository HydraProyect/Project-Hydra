using AngleSharp.Dom;
using CaeManager.Infrastructure.Identity;
using Bunit;
using Bunit.TestDoubles;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion.Queries;
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
/// detalle de los Proyectos de todos los Clientes empresariales que el usuario
/// alcanza, con el Cliente empresarial, el estado y la búsqueda como filtros en
/// la URL, y el detalle en un panel lateral en vez de una tarjeta bajo la tabla.
///
/// <para>
/// <b>Lo que esto SÍ observa:</b> los estados de la lista (sin Cliente
/// empresarial elegido, error, vacía, vacía por filtro, con datos), que la
/// búsqueda, el estado y la página viajan a <c>ObtenerProyectosQuery</c> —el
/// mediador de aquí aplica su contrato en memoria—, que «Quitar los filtros»
/// limpia la URL, qué peticiones llegan al mediador, y que el panel conserva las
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
/// de cartera ni el SQL de <c>ObtenerProyectosQuery</c> (filtros, orden y
/// paginación de verdad: Application e integración bajo RLS), ni la pestaña
/// Documentos, que delega en <c>PestanaDocumentacion</c>.
/// </para>
/// </summary>
public partial class ProyectosGen2Tests : BunitContext
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
        public List<FiltroGuardadoDto> FiltrosGuardados { get; } = [];
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

        /// <summary>
        /// El contrato de <c>ObtenerProyectosQuery</c> en memoria: sin Cliente empresarial, los de los dos;
        /// búsqueda por nombre o Centro; recuentos sin el filtro de estado; total y página con todos los
        /// filtros. Conserva el orden en que el test dio la lista. Cada fila dice su Cliente empresarial.
        /// </summary>
        private ResultadoPaginado<ProyectoListaDto> Listar(ObtenerProyectosQuery q)
        {
            var deA = Proyectos.Select(p => p with { ClienteId = ClienteId, ClienteRazonSocial = "Refrielectric S.L." });
            var deB = ProyectosClienteB.Select(p => p with { ClienteId = ClienteBId, ClienteRazonSocial = "Frigoríficos Arcos S.A." });
            var filas = (q.ClienteId is not { } cliente ? deA.Concat(deB) : cliente == ClienteBId ? deB : cliente == ClienteId ? deA : [])
                .Where(p => string.IsNullOrWhiteSpace(q.Busqueda)
                    || p.Nombre.Contains(q.Busqueda.Trim(), StringComparison.OrdinalIgnoreCase)
                    || p.CentroNombre.Contains(q.Busqueda.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();

            var recuentos = new Dictionary<string, int>
            {
                [ObtenerProyectosQuery.EstadoAbiertos] = filas.Count(p => p.EstaAbierto),
                [ObtenerProyectosQuery.EstadoCerrados] = filas.Count(p => !p.EstaAbierto),
            };
            if (q.SoloAbiertos is { } soloAbiertos)
                filas = filas.Where(p => p.EstaAbierto == soloAbiertos).ToList();

            var pagina = filas.Skip((q.Pagina - 1) * q.TamanoPagina).Take(q.TamanoPagina).ToList();
            return new ResultadoPaginado<ProyectoListaDto>(pagina, filas.Count, q.Pagina, q.TamanoPagina)
            {
                RecuentosPorEstado = q.ConRecuentosPorEstado ? recuentos : null
            };
        }

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
                ObtenerFiltrosGuardadosQuery => (IReadOnlyList<FiltroGuardadoDto>)FiltrosGuardados.ToList(),
                ObtenerClientesParaSelectorQuery => new[]
                {
                    new ClienteSelectorDto(ClienteId, "Refrielectric S.L."),
                    new ClienteSelectorDto(ClienteBId, "Frigoríficos Arcos S.A."),
                },
                ObtenerCentrosParaSelectorQuery q => (IReadOnlyList<CentroSelectorDto>)(q.ClienteId == ClienteBId ? CentrosClienteB : CentrosClienteA).ToList(),
                ObtenerProyectosQuery when FallarAlCargarProyectos => throw new InvalidOperationException("fallo simulado de la consulta"),
                ObtenerProyectosQuery q => Listar(q),
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
        DisparadorPastilla(cut, "Cliente");

    // Devuelve la tarea del clic: las pruebas de respuestas retenidas y confirmaciones
    // siguen observando todo el manejador, sin invocarlo por reflexión ni saltarse el menú.
    private static Task ElegirCliente(IRenderedComponent<Proyectos> cut, Guid clienteId) =>
        ElegirPastillaAsync(cut, "Cliente", clienteId == Guid.Empty
            ? "Todos"
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

    private static IElement FilaDe(IRenderedComponent<Proyectos> cut, ProyectoListaDto proyecto) =>
        cut.FindAll("tbody tr").Single(f => f.QuerySelector(".nombre-proyecto")!.TextContent.Trim() == proyecto.Nombre);

    private static bool PanelAbiertoEn(IRenderedComponent<Proyectos> cut, ProyectoListaDto proyecto) =>
        cut.FindAll("aside.panel-proyecto .nombre-cabecera-panel-proyecto").Any(n => n.TextContent.Trim() == proyecto.Nombre);

    /// <summary>El lápiz de la cabecera del panel: la única entrada a la edición desde que la fila no lleva menú.</summary>
    private static IElement Lapiz(IRenderedComponent<Proyectos> cut) =>
        cut.Find("aside.panel-proyecto .cabecera-panel-proyecto button[aria-label='Editar la información del proyecto']");

    /// <summary>
    /// «Cerrar proyecto» vive en el pie del panel (la fila no lleva menú): abre el panel del
    /// proyecto si no lo estaba y pulsa el botón; deja abierto el formulario de cierre.
    /// </summary>
    private static async Task CerrarDesdeElPanelAsync(IRenderedComponent<Proyectos> cut, ProyectoListaDto proyecto)
    {
        if (!PanelAbiertoEn(cut, proyecto))
            await AbrirDetalle(cut, proyecto);
        await BotonConTexto(cut, ".pie-panel-proyecto button", "Cerrar proyecto").ClickAsync(new MouseEventArgs());
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
            .And.Contain("(04/03/2026)", "la confirmación nombra la fecha de cierre que se pierde, en el formato único de la pantalla");

        await ConfirmarReapertura(cut);

        _mediator.Enviados.OfType<ReabrirProyectoCommand>().Should().ContainSingle()
            .Which.Id.Should().Be(ProyectoCerrado.Id, "se reabre el proyecto que muestra el panel");
        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle(m => m.Tono == TonoToast.Exito)
            .Which.Mensaje.Should().StartWith("Proyecto reabierto");
    }

    /// <summary>
    /// Patrón de listados (2026-10-08): la fila no lleva menú «⋯». Un clic en cualquier punto de
    /// la fila abre la vista rápida, y cada acción del menú retirado conserva un sitio: cerrar,
    /// reabrir y eliminar en el pie del panel; la página 360 en el icono del final de la fila.
    /// </summary>
    [Fact]
    public async Task La_fila_no_lleva_menu_y_un_clic_en_ella_abre_la_vista_rapida_de_ese_proyecto()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoCerrado];
        var cut = await RenderizarConClienteAsync();

        cut.FindAll("tbody .menu-acciones-disparador").Should().BeEmpty("la fila no lleva menú «⋯»");
        cut.FindAll("tbody tr").Should().OnlyContain(f => f.ClassList.Contains("fila-pulsable"));
        PanelDeDetalleAbierto(cut).Should().BeFalse();

        await FilaDe(cut, ProyectoCerrado).ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => PanelAbiertoEn(cut, ProyectoCerrado).Should().BeTrue("el clic en la fila abre su vista rápida"));
        cut.FindAll(".pie-panel-proyecto button").Select(b => b.TextContent.Trim())
            .Should().Equal(["Reabrir proyecto", "Eliminar"], "las acciones del menú retirado viven en el pie del panel");
    }

    /// <summary>
    /// El nombre es un botón dentro de la fila pulsable: corta su clic para que no llegue también a
    /// la fila. Sin el corte, un solo clic pediría el detalle dos veces.
    /// </summary>
    [Fact]
    public async Task El_clic_en_el_nombre_abre_la_vista_rapida_una_sola_vez()
    {
        _mediator.Proyectos = [ProyectoAbierto];
        var cut = await RenderizarConClienteAsync();

        await AbrirDetalle(cut, ProyectoAbierto);

        cut.WaitForAssertion(() => PanelAbiertoEn(cut, ProyectoAbierto).Should().BeTrue());
        _mediator.Enviados.OfType<ObtenerProyectoPorIdQuery>().Should().ContainSingle("el clic del nombre no sube a la fila");
    }

    /// <summary>El icono 360 del final de la fila es un enlace: no abre la vista rápida.</summary>
    [Fact]
    public async Task El_icono_360_de_la_fila_no_abre_la_vista_rapida()
    {
        _mediator.Proyectos = [ProyectoAbierto];
        var cut = await RenderizarConClienteAsync();

        // El enlace corta su clic y no tiene manejador propio: bUnit lo dice con esta excepción
        // —«nadie recibe este clic»—, que es justo la propiedad. Sin el corte llegaría a la fila.
        var clic = () => FilaDe(cut, ProyectoAbierto).QuerySelector("a.boton-360-pagina")!.ClickAsync(new MouseEventArgs());
        await clic.Should().ThrowAsync<MissingEventHandlerException>();

        PanelDeDetalleAbierto(cut).Should().BeFalse("el enlace 360 lleva a la página, no al panel");
        _mediator.Enviados.OfType<ObtenerProyectoPorIdQuery>().Should().BeEmpty();
    }

    [Fact]
    public async Task Consulta_no_ve_reabrir_en_un_proyecto_cerrado()
    {
        this.ConRolDeEscritura(Roles.Consulta);
        _mediator.Proyectos = [ProyectoCerrado];
        var cut = await RenderizarConClienteAsync();
        await AbrirDetalle(cut, ProyectoCerrado);

        cut.Find("aside.panel-proyecto .rejilla-info-proyecto").Should().NotBeNull("el panel tiene que estar pintado para que la ausencia signifique algo");
        cut.FindAll("aside.panel-proyecto button").Select(b => b.TextContent.Trim()).Should().NotContain("Reabrir proyecto");
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

    /// <summary>La respuesta de <c>ObtenerProyectosQuery</c> con esas filas en una sola página y sus recuentos.</summary>
    private static ResultadoPaginado<ProyectoListaDto> Pagina(params ProyectoListaDto[] filas) =>
        new(filas, filas.Length, 1, 20)
        {
            RecuentosPorEstado = new Dictionary<string, int>
            {
                [ObtenerProyectosQuery.EstadoAbiertos] = filas.Count(p => p.EstaAbierto),
                [ObtenerProyectosQuery.EstadoCerrados] = filas.Count(p => !p.EstaAbierto),
            }
        };

    private ObtenerProyectosQuery UltimaConsultaDeProyectos => _mediator.Enviados.OfType<ObtenerProyectosQuery>().Last();

    /// <summary>El desplegable «Cliente» o «Centro» del formulario de alta.</summary>
    private static IElement SelectDelAlta(IRenderedComponent<Proyectos> cut, string etiqueta) =>
        cut.FindComponents<CampoSelect>().Single(c => c.Instance.Etiqueta == etiqueta).Find("select");

    private static List<string> OpcionesDelAlta(IRenderedComponent<Proyectos> cut, string etiqueta) =>
        SelectDelAlta(cut, etiqueta).QuerySelectorAll("option").Select(o => o.TextContent.Trim()).ToList();

    private static Task PulsarNuevoProyectoAsync(IRenderedComponent<Proyectos> cut) =>
        cut.FindAll("button").First(b => b.TextContent.Trim() == "+ Nuevo proyecto").ClickAsync(new MouseEventArgs());

    private static readonly TimeSpan Paciencia = TimeSpan.FromSeconds(10);

    private static string ValorDeCampoInfo(IElement ambito, string etiqueta) =>
        ambito.QuerySelectorAll(".campo-info")
            .Single(c => c.QuerySelector(".campo-info-etiqueta")!.TextContent.Trim() == etiqueta)
            .QuerySelector(".campo-info-valor")!.TextContent.Trim();

    private string Uri => Services.GetRequiredService<NavigationManager>().Uri;

    /// <summary>
    /// El Cliente empresarial es un filtro opcional. «Todos» pide la lista sin Cliente empresarial —el
    /// alcance lo aplica la consulta, no la pantalla— y elegir uno la acota a los suyos.
    /// </summary>
    [Fact]
    public async Task Todos_pide_la_lista_sin_Cliente_empresarial_y_elegir_uno_la_acota()
    {
        _mediator.Proyectos = [ProyectoAbierto];
        _mediator.ProyectosClienteB = [ProyectoDeB];
        var cut = await RenderizarConClienteAsync();
        NombresEnLaTabla(cut).Should().Equal([ProyectoAbierto.Nombre], "control positivo: con un Cliente empresarial elegido solo salen los suyos");

        await SelectorDeCliente(cut).ClickAsync(new MouseEventArgs());
        var panelId = SelectorDeCliente(cut).GetAttribute("aria-controls")!;
        var opciones = cut.Find("#" + panelId).QuerySelectorAll("[role=menuitemradio]").Select(i => i.TextContent.Trim()).ToList();
        opciones.Should().Equal("Todos", "Refrielectric S.L.", "Frigoríficos Arcos S.A.");
        await ElegirCliente(cut, Guid.Empty);

        UltimaConsultaDeProyectos.ClienteId.Should().BeNull("«Todos» no elige ningún Cliente empresarial");
        NombresEnLaTabla(cut).Should().BeEquivalentTo([ProyectoAbierto.Nombre, ProyectoDeB.Nombre]);
        cut.Markup.Should().Contain("+ Nuevo proyecto", "el alta pide el Cliente empresarial en su formulario");

        await ElegirCliente(cut, ClienteBId);
        UltimaConsultaDeProyectos.ClienteId.Should().Be(ClienteBId);
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
        filas[1].QuerySelector(".proyecto-centro")!.TextContent.Should().NotContain("Refrielectric",
            "con un Cliente empresarial en el filtro, repetirlo en cada fila sobra");
        // Columna «Fechas»: «inicio → fin» o «inicio → sin fecha de fin».
        var fechasConFin = filas[1].QuerySelector(".proyecto-plazo")!.TextContent;
        fechasConFin.Should().Contain(ProyectoAbierto.FechaInicio.ToString("dd/MM/yyyy"))
            .And.Contain("→")
            .And.Contain(ProyectoAbierto.FechaFinPrevista!.Value.ToString("dd/MM/yyyy"));
        var fechasSinFin = filas[0].QuerySelector(".proyecto-plazo")!.TextContent;
        fechasSinFin.Should().Contain(ProyectoAbierto2.FechaInicio.ToString("dd/MM/yyyy"))
            .And.Contain("→")
            .And.Contain("sin fecha de fin")
            .And.NotContain("prevista", "el rótulo largo era el de la columna «Plazo» anterior");

        await AbrirDetalle(cut, ProyectoAbierto);
        _mediator.Enviados.OfType<ObtenerProyectoPorIdQuery>().Last().Id.Should().Be(ProyectoAbierto.Id);
        cut.Find(".nombre-cabecera-panel-proyecto").TextContent.Trim().Should().Be(ProyectoAbierto.Nombre);
    }
    // ------------------------------------------------------------------ estados de la lista

    /// <summary>
    /// La lista no obliga a elegir Cliente empresarial: la primera carga trae los Proyectos de todos los que
    /// el usuario alcanza, con la franja de estado y el contador puestos, y cada fila dice de quién es.
    /// </summary>
    [Fact]
    public void Sin_Cliente_empresarial_elegido_lista_los_de_todos_con_franja_y_contador_desde_la_primera_carga()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoCerrado];
        _mediator.ProyectosClienteB = [ProyectoDeB];

        var cut = Renderizar();

        cut.WaitForAssertion(() => NombresEnLaTabla(cut).Should()
            .BeEquivalentTo([ProyectoAbierto.Nombre, ProyectoCerrado.Nombre, ProyectoDeB.Nombre]));
        _mediator.Enviados.OfType<ObtenerProyectosQuery>().Should().ContainSingle("la primera carga es una sola consulta")
            .Which.Should().Match<ObtenerProyectosQuery>(q => q.ClienteId == null && q.Pagina == 1 && q.ConRecuentosPorEstado);
        cut.Find(".cabecera-listado-contador").TextContent.Trim().Should().Be("3");
        cut.RotulosDeFranja().Should().Equal("Todos", "Abiertos", "Cerrados");
        cut.BotonDeFranja("Todos").RecuentoDeFranja().Should().Be(3);
        cut.BotonDeFranja("Abiertos").RecuentoDeFranja().Should().Be(2);
        cut.BotonDeFranja("Cerrados").RecuentoDeFranja().Should().Be(1);
        FilaDe(cut, ProyectoAbierto).QuerySelector(".proyecto-centro")!.TextContent.Trim()
            .Should().Be("Centro Logístico Norte · Refrielectric S.L.");
        FilaDe(cut, ProyectoDeB).QuerySelector(".proyecto-centro")!.TextContent.Trim()
            .Should().Be("Planta Barakaldo · Frigoríficos Arcos S.A.", "sin filtro de Cliente empresarial, la fila dice de quién es");
        cut.Markup.Should().Contain("+ Nuevo proyecto").And.NotContain("Elige un Cliente");
    }

    [Fact]
    public void Sin_Cliente_empresarial_y_sin_ningun_proyecto_invita_a_crear_sin_hablar_de_un_Cliente()
    {
        var cut = Renderizar();

        cut.WaitForAssertion(() => cut.Find(".estado-vacio h3").TextContent.Should().Be("Sin proyectos"));
        cut.Markup.Should().Contain("Todavía no hay ningún proyecto de obra o instalación.")
            .And.NotContain("Este Cliente todavía");
        cut.RotulosDeFranja().Should().Equal(["Todos", "Abiertos", "Cerrados"], "la franja está aunque la lista esté vacía");
        cut.Find(".estado-vacio button").TextContent.Trim().Should().Be("+ Nuevo proyecto");
    }

    [Fact]
    public async Task Cliente_sin_proyectos_y_sin_filtros_invita_a_crear_el_primero()
    {
        var cut = await RenderizarConClienteAsync();

        cut.Find(".estado-vacio h3").TextContent.Should().Be("Sin proyectos");
        cut.Markup.Should().Contain("Este Cliente todavía no tiene ningún proyecto de obra o instalación.");
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
    /// La búsqueda y el estado los aplica la consulta: con cero filas y un filtro puesto la pantalla no
    /// sabe si sin él habría alguna. El vacío por filtro dice solo que ninguno coincide —nunca que el
    /// Cliente empresarial «sí tiene proyectos»— y ofrece quitar los filtros, no crear.
    /// </summary>
    [Fact]
    public async Task El_vacio_por_filtro_no_afirma_que_haya_otros_proyectos()
    {
        var cut = await RenderizarConClienteAsync("proyectos?q=calderas");

        cut.Find(".estado-vacio h3").TextContent.Should().Be("Ningún proyecto con este filtro");
        cut.Markup.Should().Contain("Ningún proyecto coincide con la búsqueda ni con el estado seleccionado.")
            .And.NotContain("sí tiene proyectos");
        cut.Find(".estado-vacio button").TextContent.Trim().Should().Be("Quitar los filtros");
    }

    [Fact]
    public async Task El_estado_sin_coincidencias_tambien_es_vacio_por_filtro_y_la_franja_sigue_contando()
    {
        _mediator.Proyectos = [ProyectoAbierto];

        var cut = await RenderizarConClienteAsync("proyectos?estado=cerrados");

        cut.Find(".estado-vacio h3").TextContent.Should().Be("Ningún proyecto con este filtro");
        UltimaConsultaDeProyectos.SoloAbiertos.Should().BeFalse("el estado lo filtra la consulta");
        cut.BotonDeFranja("Abiertos").RecuentoDeFranja().Should().Be(1, "la franja dice lo que habría al marcar el otro botón");
        cut.BotonDeFranja("Cerrados").RecuentoDeFranja().Should().Be(0);
    }

    [Fact]
    public async Task Filtro_de_estado_cerrados_deja_solo_los_cerrados_y_lo_cuenta()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoCerrado];

        var cut = await RenderizarConClienteAsync("proyectos?estado=cerrados");

        var nombres = cut.FindAll("tbody .nombre-proyecto").Select(b => b.TextContent.Trim()).ToList();
        nombres.Should().Equal(ProyectoCerrado.Nombre);
        cut.Find(".cabecera-listado-contador").TextContent.Trim().Should().Be("1");
        UltimaConsultaDeProyectos.SoloAbiertos.Should().BeFalse("el estado lo filtra la consulta, antes de paginar");
    }

    [Fact]
    public async Task La_busqueda_encuentra_tambien_por_nombre_de_centro()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoCerrado];

        var cut = await RenderizarConClienteAsync("proyectos?q=portugalete");

        var nombres = cut.FindAll("tbody .nombre-proyecto").Select(b => b.TextContent.Trim()).ToList();
        nombres.Should().Equal(ProyectoCerrado.Nombre);
        UltimaConsultaDeProyectos.Busqueda.Should().Be("portugalete", "la búsqueda la aplica la consulta");
    }

    // ----------------------- El Cliente empresarial elegido viaja en la URL (T20)

    [Fact]
    public async Task Elegir_el_Cliente_empresarial_lo_escribe_en_la_url_y_volver_a_Todos_lo_quita()
    {
        var cut = await RenderizarConClienteAsync();

        Uri.Should().Contain($"cliente={ClienteId}");

        await ElegirCliente(cut, Guid.Empty);

        Uri.Should().NotContain("cliente=");
    }

    /// <summary>Recargar o compartir el enlace abre la lista de ese Cliente empresarial, sin volver a elegirlo.</summary>
    [Fact]
    public void Un_enlace_con_el_Cliente_empresarial_abre_su_lista()
    {
        _mediator.Proyectos = [ProyectoAbierto];

        var cut = Renderizar($"proyectos?cliente={ClienteId}");

        cut.WaitForAssertion(() => cut.FindAll("tbody .nombre-proyecto").Select(b => b.TextContent.Trim())
            .Should().Equal(ProyectoAbierto.Nombre));
        SelectorDeCliente(cut).GetAttribute("aria-label").Should().Be("Cliente: Refrielectric S.L.");
        _mediator.Enviados.OfType<ObtenerProyectosQuery>().Should().ContainSingle(q => q.ClienteId == ClienteId,
            "la carga inicial y la primera pasada de parámetros no duplican la consulta");
        ((BunitNavigationManager)Services.GetRequiredService<NavigationManager>()).History.Should().ContainSingle(
            // bUnit sustituye la entrada cuando la página navega con replace: la única que queda
            // tiene que ser la del arnés (sin replace), no una escrita por la página.
            h => !h.Options.ReplaceHistoryEntry,
            "abrir el enlace no navega: la URL ya dice el Cliente, y un NavigateTo en el prerender "
            + "es una redirección HTTP a la misma dirección, en bucle");
    }

    /// <summary>
    /// La URL no puede elegir un Cliente empresarial que el selector no ofrece a este usuario: cuenta como
    /// ausente. La lista es la de todos los que alcanza y el Id ajeno no viaja a la consulta.
    /// </summary>
    [Fact]
    public void Un_Cliente_empresarial_de_la_url_que_no_esta_en_el_selector_no_se_elige_ni_viaja_a_la_consulta()
    {
        _mediator.Proyectos = [ProyectoAbierto];

        var cut = Renderizar($"proyectos?cliente={Guid.NewGuid()}");

        cut.WaitForAssertion(() => NombresEnLaTabla(cut).Should().Equal([ProyectoAbierto.Nombre], "control positivo: la lista cargó"));
        SelectorDeCliente(cut).GetAttribute("aria-label").Should().NotContain("Refrielectric");
        _mediator.Enviados.OfType<ObtenerProyectosQuery>().Should().NotBeEmpty()
            .And.OnlyContain(q => q.ClienteId == null, "el Id de la URL no es autoridad");
    }

    [Fact]
    public async Task Quitar_los_filtros_los_limpia_tambien_de_la_url_con_el_Cliente_empresarial_y_devuelve_la_lista()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoCerrado];
        _mediator.ProyectosClienteB = [ProyectoDeB];
        var cut = await RenderizarConClienteAsync("proyectos?q=calderas&estado=cerrados");
        cut.Find(".estado-vacio h3").TextContent.Should().Be("Ningún proyecto con este filtro", "es el punto de partida de este caso");

        await cut.Find(".estado-vacio button").ClickAsync(new MouseEventArgs());

        Uri.Should().NotContain("q=").And.NotContain("estado=",
            "OnParametersSet re-sincroniza desde la URL: dejar ahí los filtros los devolvería en la siguiente navegación");
        Uri.Should().NotContain("cliente=",
            "el Cliente empresarial es un filtro más desde que la lista no obliga a elegirlo: quitar los filtros también lo quita");
        SelectorDeCliente(cut).GetAttribute("aria-label").Should().NotContain("Refrielectric");
        UltimaConsultaDeProyectos.Should().Match<ObtenerProyectosQuery>(q => q.ClienteId == null && q.Busqueda == null && q.SoloAbiertos == null);
        cut.FindAll("tbody .nombre-proyecto").Select(b => b.TextContent.Trim())
            .Should().BeEquivalentTo([ProyectoAbierto.Nombre, ProyectoCerrado.Nombre, ProyectoDeB.Nombre]);
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
    public async Task La_pagina_360_se_alcanza_con_el_icono_360_de_la_fila_y_con_el_de_la_cabecera_del_panel()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoCerrado];
        var cut = await RenderizarConClienteAsync();
        var destino = $"/proyectos/{ProyectoCerrado.Id}";

        var enlaceDeLaFila = FilaDe(cut, ProyectoCerrado).QuerySelector("a.boton-360-pagina")!;
        enlaceDeLaFila.GetAttribute("href").Should().Be(destino);
        enlaceDeLaFila.GetAttribute("aria-label").Should().Contain(ProyectoCerrado.Nombre, "con un icono por fila, el nombre accesible dice cuál abre");

        await AbrirDetalle(cut, ProyectoCerrado);
        cut.Find("aside.panel-proyecto .cabecera-panel-proyecto a.boton-360-pagina").GetAttribute("href").Should().Be(destino);
    }

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
        cut.FindAll(".pie-panel-proyecto button").Select(b => b.TextContent.Trim()).Should().Equal("Reabrir proyecto", "Eliminar");

        await AbrirDetalle(cut, ProyectoAbierto);
        cut.FindAll(".pie-panel-proyecto button").Select(b => b.TextContent.Trim()).Should().Equal("Cerrar proyecto", "Eliminar");

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

        await CerrarDesdeElPanelAsync(cut, ProyectoAbierto);
        await ConfirmarCierre(cut);

        _mediator.Enviados.OfType<CerrarProyectoCommand>().Should().ContainSingle()
            .Which.Id.Should().Be(AbiertoId, "el comando lleva el id de la fila, no el del detalle abierto");
    }

    /// <summary>
    /// Antes el modal de cierre reutilizaba la selección del detalle: cerrar
    /// desde el menú de una fila, sin detalle abierto, hacía aparecer el
    /// detalle vacío con «No pudimos cargar este proyecto».
    /// </summary>
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

        cut.FindAll("aside.panel-proyecto").Should().BeEmpty("el panel se cerró a mano y la respuesta tardía no lo reabre");
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
        await Lapiz(cut).ClickAsync(new MouseEventArgs());
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
        await Resolver(cut, proyectosB, Pagina(ProyectoDeB));
        await cambioB.WaitAsync(Paciencia);
        await Resolver(cut, proyectosA, Pagina(ProyectoAbierto));
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
        await Resolver(cut, proyectosA, Pagina(ProyectoAbierto));
        await cambioA.WaitAsync(Paciencia);

        cut.FindAll(".esqueleto-lista").Should().NotBeEmpty("la carga de B sigue en curso");
        cut.Markup.Should().NotContain("Sin proyectos");
        NombresEnLaTabla(cut).Should().BeEmpty();

        await Resolver(cut, proyectosB, Pagina(ProyectoDeB));
        await cambioB.WaitAsync(Paciencia);
        NombresEnLaTabla(cut).Should().Equal([ProyectoDeB.Nombre]);
    }

    /// <summary>
    /// Los Centros del alta son los del Cliente empresarial elegido EN EL FORMULARIO. Si se cambia con la
    /// carga del anterior en vuelo, su respuesta tardía no puede ofrecer sus Centros bajo el nuevo.
    /// </summary>
    [Fact]
    public async Task Los_centros_del_Cliente_empresarial_anterior_del_alta_que_llegan_tarde_no_se_ofrecen_bajo_el_nuevo()
    {
        _mediator.CentrosClienteA = [CentroDeA];
        _mediator.CentrosClienteB = [CentroDeB];
        var cut = Renderizar();
        await PulsarNuevoProyectoAsync(cut);
        var centrosA = _mediator.Retener(r => r is ObtenerCentrosParaSelectorQuery q && q.ClienteId == ClienteId);

        var cambioA = SelectDelAlta(cut, "Cliente").ChangeAsync(new ChangeEventArgs { Value = ClienteId.ToString() });
        await SelectDelAlta(cut, "Cliente").ChangeAsync(new ChangeEventArgs { Value = ClienteBId.ToString() }).WaitAsync(Paciencia);
        OpcionesDelAlta(cut, "Centro").Should().Contain(CentroDeB.Nombre, "control positivo: los del Cliente empresarial vigente ya están");
        await Resolver(cut, centrosA, (IReadOnlyList<CentroSelectorDto>)[CentroDeA]);
        await cambioA.WaitAsync(Paciencia);

        OpcionesDelAlta(cut, "Centro").Should().Contain(CentroDeB.Nombre)
            .And.NotContain(CentroDeA.Nombre, "el alta cuelga del Cliente empresarial vigente: un Centro de A lo colgaría del equivocado");
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

        await CerrarDesdeElPanelAsync(cut, ProyectoAbierto);
        var confirmacion = ConfirmarCierre(cut);
        await ElegirCliente(cut, ClienteBId).WaitAsync(Paciencia);
        await Resolver(cut, recargaA, Pagina(ProyectoAbierto));
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
    /// <summary>
    /// La franja de estado de Proyectos: «Todos», «Abiertos» y «Cerrados» con cuántos hay de cada uno; marcar
    /// los dos los enseña todos, y la selección viaja en la URL (<c>estado=abiertos,cerrados</c>).
    /// </summary>
    [Fact]
    public async Task La_franja_de_estado_cuenta_abiertos_y_cerrados_filtra_y_viaja_en_la_url()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoCerrado];
        var cut = await RenderizarConClienteAsync();
        var navegacion = Services.GetRequiredService<NavigationManager>();

        cut.RotulosDeFranja().Should().Equal("Todos", "Abiertos", "Cerrados");
        cut.BotonDeFranja("Todos").RecuentoDeFranja().Should().Be(2);
        cut.BotonDeFranja("Abiertos").RecuentoDeFranja().Should().Be(1);
        cut.BotonDeFranja("Cerrados").RecuentoDeFranja().Should().Be(1);

        await cut.BotonDeFranja("Cerrados").ClickAsync(new MouseEventArgs());

        NombresEnLaTabla(cut).Should().Equal(ProyectoCerrado.Nombre);
        navegacion.Uri.Should().Contain("estado=cerrados");
        cut.MarcadosEnFranja().Should().Equal("Cerrados");
        cut.BotonDeFranja("Abiertos").RecuentoDeFranja().Should().Be(1, "las cifras no llevan el filtro de estado: dicen lo que habría al marcar ese botón");

        await cut.BotonDeFranja("Abiertos").ClickAsync(new MouseEventArgs());

        NombresEnLaTabla(cut).Should().HaveCount(2, "con los dos estados marcados pasa cualquiera");
        System.Uri.UnescapeDataString(navegacion.Uri).Should().Contain("estado=cerrados,abiertos");
        cut.MarcadosEnFranja().Should().Equal("Abiertos", "Cerrados");
    }

    [Fact]
    public async Task El_foco_de_una_fila_que_el_filtro_esconde_no_sobrevive_ni_reaparece()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoCerrado];
        var cut = await RenderizarConClienteAsync();
        await Atajo(cut, "j");
        await Atajo(cut, "j");
        FilasEnfocadas(cut).Should().Equal([ProyectoCerrado.Nombre]);

        // El estado se filtra en la franja: «Abiertos» esconde la fila enfocada (la del Proyecto cerrado).
        await cut.BotonDeFranja("Abiertos").ClickAsync(new MouseEventArgs());

        cut.MarcadosEnFranja().Should().Equal("Abiertos");
        NombresEnLaTabla(cut).Should().Equal([ProyectoAbierto.Nombre], "control: el filtro esconde de verdad la fila enfocada");
        FilasEnfocadas(cut).Should().BeEmpty();

        await cut.BotonDeFranja("Todos").ClickAsync(new MouseEventArgs());

        cut.MarcadosEnFranja().Should().Equal("Todos");

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
        await PulsarNuevoProyectoAsync(cut);
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
        await SelectDelAlta(cut, "Centro").ChangeAsync(new ChangeEventArgs { Value = CentroDeA.Id.ToString() });
        await EscribirNombreDelProyectoAsync(cut, "Montaje cámaras 2026");

        await BotonConTexto(cut, ".drawer-panel button", "Crear proyecto").ClickAsync(new MouseEventArgs());

        _mediator.Enviados.OfType<CrearProyectoCommand>().Should().ContainSingle("el caso solo vale si se creó")
            .Which.Should().Match<CrearProyectoCommand>(c => c.ClienteId == ClienteId && c.CentroId == CentroDeA.Id,
                "el alta viene con el Cliente empresarial del filtro puesto");
        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "lo escrito ya está guardado");
    }

    [Fact]
    public async Task Aviso_cerrar_proyecto_con_la_fecha_de_hoy_puesta_no_pregunta()
    {
        _mediator.Proyectos = [ProyectoAbierto];
        var cut = await RenderizarConClienteAsync();
        await CerrarDesdeElPanelAsync(cut, ProyectoAbierto);
        cut.FindAll("[role=dialog]").Should().NotBeEmpty("el test necesita el modal abierto");

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "la fecha de cierre de hoy viene puesta: no es un cambio");
    }

    [Fact]
    public async Task Aviso_cerrar_proyecto_con_otra_fecha_pregunta_al_salir()
    {
        _mediator.Proyectos = [ProyectoAbierto];
        var cut = await RenderizarConClienteAsync();
        await CerrarDesdeElPanelAsync(cut, ProyectoAbierto);
        await cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Fecha de cierre")
            .Find("input").InputAsync(new ChangeEventArgs { Value = "2020-01-01" });

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task Aviso_confirmar_el_cierre_con_otra_fecha_deja_salir_sin_preguntar()
    {
        _mediator.Proyectos = [ProyectoAbierto];
        var cut = await RenderizarConClienteAsync();
        await CerrarDesdeElPanelAsync(cut, ProyectoAbierto);
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
        await CerrarDesdeElPanelAsync(cut, ProyectoAbierto);
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
        await Lapiz(cut).ClickAsync(new MouseEventArgs());
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
        SelectorDeCliente(cut).GetAttribute("aria-label").Should().Be("Cliente: Refrielectric S.L.",
            "cancelar conserva también la selección visible de la pastilla");
        ValorDelCampo(cut, "Nombre").Should().Be("Otro nombre", "y conserva lo escrito");

        var segundoCierre = CerrarElPanelAsync(cut);
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue());
        await PulsarEnLaPreguntaAsync(cut, "Salir y descartar");
        await segundoCierre.WaitAsync(Paciencia);

        PanelDeDetalleAbierto(cut).Should().BeFalse("«Salir y descartar» cierra el panel");
    }

    /// <summary>
    /// Medido en CI (E2E <c>ProyectosFase1SelectorTests</c>): reescribir la URL al seguir editando
    /// es una navegación con el panel sin guardar, y la pregunta volvía a salir sola.
    /// </summary>
    [Fact]
    public async Task Aviso_cambiar_de_Cliente_empresarial_y_seguir_editando_no_navega()
    {
        var cut = await AbrirLaEdicionDelDetalleAsync();
        await EscribirEnElPanelAsync(cut, "Otro nombre");
        var historial = ((BunitNavigationManager)Services.GetRequiredService<NavigationManager>()).History;
        var navegacionesAntes = historial.Count;
        var uriAntes = Uri;

        var cambio = ElegirCliente(cut, Guid.Empty);
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue("cambiar de Cliente cierra el panel"));
        await PulsarEnLaPreguntaAsync(cut, "Seguir editando");
        await cambio.WaitAsync(Paciencia);

        historial.Count.Should().Be(navegacionesAntes, "la URL no ha cambiado: no hay nada que reescribir");
        Uri.Should().Be(uriAntes);
        PreguntaAbierta(cut).Should().BeFalse();
        ValorDelCampo(cut, "Nombre").Should().Be("Otro nombre");
    }

    /// <summary>
    /// Una navegación de la aplicación (<c>NavigateTo</c>) a la misma pantalla con otro Cliente empresarial,
    /// otra búsqueda y otro estado, con la edición del panel a medias. La búsqueda y el estado los filtra la
    /// consulta: una pantalla que tomase los filtros de la URL nueva sin pedir la lista enseñaría las
    /// pastillas de una vista con las filas, el contador y la franja de otra. «Seguir editando» lo deja todo
    /// como estaba y «Salir y descartar» lo cambia todo a la vez; la segunda mitad es además el control
    /// positivo de la primera: esa misma navegación, cuando pasa, sí llega a la página. Aquí quien detiene
    /// la navegación es el aviso; «atrás» del navegador no pasa por él y lo mide el E2E
    /// <c>ProyectosFase1SelectorTests</c>.
    /// </summary>
    [Fact]
    public async Task Aviso_navegar_a_otros_filtros_con_la_edicion_a_medias_no_separa_la_url_las_pastillas_y_la_lista()
    {
        var cut = await AbrirLaEdicionDelDetalleAsync();
        await EscribirEnElPanelAsync(cut, "Otro nombre");
        var uriAntes = Uri;
        var consultasAntes = _mediator.Enviados.OfType<ObtenerProyectosQuery>().Count();
        var destino = $"proyectos?cliente={ClienteBId}&q=nave&estado=cerrados";

        // Sin esperar la navegación: se afirma que pregunta antes de esperar nada que dependa de la respuesta.
        var navegacion = cut.InvokeAsync(() => Navegacion.NavigateTo(destino));
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue("la navegación iba a tirar lo escrito"));
        _mediator.Enviados.OfType<ObtenerProyectosQuery>().Count().Should().Be(consultasAntes,
            "no se pide otra lista mientras pregunta");

        await PulsarEnLaPreguntaAsync(cut, "Seguir editando");
        await navegacion.WaitAsync(Paciencia);

        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeFalse());
        Uri.Should().Be(uriAntes, "«Seguir editando» deja la URL diciendo la vista que sigue en pantalla");
        SelectorDeCliente(cut).GetAttribute("aria-label").Should().Be("Cliente: Refrielectric S.L.");
        ValorDelBuscador(cut).Should().BeEmpty("la búsqueda de la URL que no se aceptó no se queda en el campo");
        cut.MarcadosEnFranja().Should().Equal("Todos");
        _mediator.Enviados.OfType<ObtenerProyectosQuery>().Count().Should().Be(consultasAntes,
            "la lista a la vista es la de la consulta anterior: no se ha pedido otra");
        UltimaConsultaDeProyectos.Should().Match<ObtenerProyectosQuery>(
            q => q.ClienteId == ClienteId && q.Busqueda == null && q.SoloAbiertos == null,
            "y esa consulta es la de las pastillas y la URL");
        ValorDelCampo(cut, "Nombre").Should().Be("Otro nombre", "«Seguir editando» conserva lo escrito");

        var segunda = cut.InvokeAsync(() => Navegacion.NavigateTo(destino));
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue());
        await PulsarEnLaPreguntaAsync(cut, "Salir y descartar");
        await segunda.WaitAsync(Paciencia);

        cut.WaitForAssertion(() => UltimaConsultaDeProyectos.Should().Match<ObtenerProyectosQuery>(
            q => q.ClienteId == ClienteBId && q.Busqueda == "nave" && q.SoloAbiertos == false,
            "descartando, la navegación llega y la lista se pide con los tres filtros de la URL"));
        Uri.Should().EndWith(destino);
        SelectorDeCliente(cut).GetAttribute("aria-label").Should().Be("Cliente: Frigoríficos Arcos S.A.");
        ValorDelBuscador(cut).Should().Be("nave");
        cut.MarcadosEnFranja().Should().Equal("Cerrados");
        PanelDeDetalleAbierto(cut).Should().BeFalse("el panel era de un Proyecto de la lista anterior");
        // La última consulta no distingue una carga de dos con los mismos filtros: se cuentan.
        _mediator.Enviados.OfType<ObtenerProyectosQuery>().Count().Should().Be(consultasAntes + 1,
            "«Salir y descartar» pide la lista una sola vez, con los tres filtros de la URL ya tomados");
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
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue("cambiar de Cliente cierra el panel y tira lo escrito"));
        _mediator.Enviados.OfType<ObtenerProyectosQuery>().Count().Should().Be(consultasDeProyectosAntes,
            "no se cambia de Cliente mientras pregunta");

        await PulsarEnLaPreguntaAsync(cut, "Seguir editando");
        await cambio.WaitAsync(Paciencia);

        PanelDeDetalleAbierto(cut).Should().BeTrue("«Seguir editando» conserva el panel");
        SelectorDeCliente(cut).GetAttribute("aria-label").Should().Be("Cliente: Refrielectric S.L.",
            "cancelar conserva también la selección visible de la pastilla");
        ValorDelCampo(cut, "Nombre").Should().Be("Otro nombre");
        _mediator.Enviados.OfType<ObtenerProyectosQuery>().Count().Should().Be(consultasDeProyectosAntes,
            "seguir editando no recarga la lista del otro Cliente");

        var segundo = ElegirCliente(cut, ClienteBId);
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue());
        await PulsarEnLaPreguntaAsync(cut, "Salir y descartar");
        await segundo.WaitAsync(Paciencia);

        PanelDeDetalleAbierto(cut).Should().BeFalse("descartar cambia de Cliente y cierra el panel");
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
        SelectorDeCliente(cut).GetAttribute("aria-label").Should().Be("Cliente: Refrielectric S.L.",
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

    /// <summary>
    /// Pulsa una acción del proyecto donde vive desde que la fila no lleva menú, sin esperar a lo que
    /// dispare: «Detalles» es el clic en la fila; cerrar, reabrir y eliminar, el pie de su panel
    /// (que se abre antes si no lo estaba).
    /// </summary>
    private static async Task<Task> PulsarAccionDelProyectoAsync(IRenderedComponent<Proyectos> cut, ProyectoListaDto proyecto, string accion)
    {
        if (accion == "Detalles")
            return FilaDe(cut, proyecto).ClickAsync(new MouseEventArgs());

        if (!PanelAbiertoEn(cut, proyecto))
            await AbrirDetalle(cut, proyecto);
        return BotonConTexto(cut, ".pie-panel-proyecto button", accion).ClickAsync(new MouseEventArgs());
    }

    /// <summary>Elimina desde el pie del panel y confirma en el diálogo (su botón de confirmar dice «Eliminar»).</summary>
    private async Task EliminarDesdeElPanelAsync(IRenderedComponent<Proyectos> cut, ProyectoListaDto proyecto, bool descartandoLoEscrito = false)
    {
        var accion = await PulsarAccionDelProyectoAsync(cut, proyecto, "Eliminar");
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

        await EliminarDesdeElPanelAsync(cut, ProyectoAbierto, descartandoLoEscrito: true);

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "el proyecto con el alta a medias ya no existe: no queda nada que perder");
    }

    [Fact]
    public async Task Aviso_eliminar_el_proyecto_abierto_con_el_alta_de_tecnico_a_medias_no_pregunta_al_abrir_otro()
    {
        var cut = await AbrirElAltaDeTecnicoAsync();
        await CambiarLaFechaDeAltaAsync(cut);

        await EliminarDesdeElPanelAsync(cut, ProyectoAbierto, descartandoLoEscrito: true);

        await ComprobarQueTerminaSinPreguntarAsync(cut, AbrirDetalle(cut, ProyectoAbierto2), "el alta a medias era del proyecto eliminado");
        cut.Find(".nombre-cabecera-panel-proyecto").TextContent.Should().Be(ProyectoAbierto2.Nombre);
    }

    // ---- Enter y el clic en otra fila con la edición de información sucia (ManejarAtajoAsync y la fila → AbrirDetalleConAvisoAsync)

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
    public async Task Aviso_clic_en_otra_fila_con_la_edicion_a_medias_pregunta_seguir_conserva_y_descartar_abre_el_otro()
    {
        var cut = await AbrirLaEdicionDelDetalleAsync();
        await EscribirEnElPanelAsync(cut, "Otro nombre");

        var apertura = await PulsarAccionDelProyectoAsync(cut, ProyectoAbierto2, "Detalles");
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue("el clic en otra fila tira lo escrito"));
        await PulsarEnLaPreguntaAsync(cut, "Seguir editando");
        await apertura.WaitAsync(Paciencia);
        ValorDelCampo(cut, "Nombre").Should().Be("Otro nombre", "«Seguir editando» conserva la edición");

        var segunda = await PulsarAccionDelProyectoAsync(cut, ProyectoAbierto2, "Detalles");
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue());
        await PulsarEnLaPreguntaAsync(cut, "Salir y descartar");
        await segunda.WaitAsync(Paciencia);

        cut.Find(".nombre-cabecera-panel-proyecto").TextContent.Should().Be(ProyectoAbierto2.Nombre, "descartar abre el otro proyecto");
    }

    [Fact]
    public async Task Aviso_clic_en_otra_fila_con_la_edicion_sin_tocar_no_pregunta()
    {
        var cut = await AbrirLaEdicionDelDetalleAsync();

        var apertura = await PulsarAccionDelProyectoAsync(cut, ProyectoAbierto2, "Detalles");

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
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue("cambiar de Cliente tira el alta a medias"));
        _mediator.Enviados.OfType<ObtenerProyectosQuery>().Count().Should().Be(consultasDeProyectosAntes,
            "no se cambia de Cliente mientras pregunta");

        await PulsarEnLaPreguntaAsync(cut, "Seguir editando");
        await cambio.WaitAsync(Paciencia);
        PanelDeDetalleAbierto(cut).Should().BeTrue("«Seguir editando» conserva el panel");
        SelectorDeCliente(cut).GetAttribute("aria-label").Should().Be("Cliente: Refrielectric S.L.",
            "cancelar conserva también la selección visible de la pastilla");
        ValorDelCampo(cut, "Fecha de alta").Should().Be("2020-01-01");
        _mediator.Enviados.OfType<ObtenerProyectosQuery>().Count().Should().Be(consultasDeProyectosAntes);

        var segundo = ElegirCliente(cut, ClienteBId);
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue());
        await PulsarEnLaPreguntaAsync(cut, "Salir y descartar");
        await segundo.WaitAsync(Paciencia);

        PanelDeDetalleAbierto(cut).Should().BeFalse("descartar cambia de Cliente y cierra el panel");
        _mediator.Enviados.OfType<ObtenerProyectosQuery>().Last().ClienteId.Should().Be(ClienteBId);
    }

    // ---- El modal de cierre pregunta solo por lo suyo

    [Fact]
    public async Task Aviso_el_modal_de_cierre_con_otra_fecha_pregunta_al_cerrar_con_la_X()
    {
        _mediator.Proyectos = [ProyectoAbierto];
        var cut = await RenderizarConClienteAsync();
        await CerrarDesdeElPanelAsync(cut, ProyectoAbierto);
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

    // Desde que la fila no lleva menú, cerrar, reabrir y eliminar solo se alcanzan en el pie del panel,
    // que no se pinta mientras se edita la información. Lo que puede quedar a medias con el pie a la
    // vista es el alta de técnico: es el caso que cubren estos tests.
    private async Task<IRenderedComponent<Proyectos>> AbrirElAltaDeTecnicoDelCerradoAsync()
    {
        _mediator.Proyectos = [ProyectoCerrado, ProyectoAbierto2];
        var cut = await RenderizarConClienteAsync();
        await AbrirDetalle(cut, ProyectoCerrado);
        await cut.FindAll("button[role=tab]").Single(b => b.TextContent.Trim() == "Técnicos").ClickAsync(new MouseEventArgs());
        await BotonConTexto(cut, ".acciones-seccion button", "+ Asignar técnico").ClickAsync(new MouseEventArgs());
        cut.FindComponents<CampoTexto>().Should().Contain(c => c.Instance.Etiqueta == "Fecha de alta", "el test necesita el alta de técnico abierta");
        return cut;
    }

    // ---- Eliminar el proyecto abierto

    [Fact]
    public async Task Aviso_eliminar_el_proyecto_abierto_con_el_alta_de_tecnico_a_medias_pregunta_seguir_conserva_y_descartar_sigue_con_la_eliminacion()
    {
        var cut = await AbrirElAltaDeTecnicoAsync();
        await CambiarLaFechaDeAltaAsync(cut);

        // Sin await: la acción queda pendiente de la respuesta del aviso; se afirma antes de esperarla.
        var accion = await PulsarAccionDelProyectoAsync(cut, ProyectoAbierto, "Eliminar");
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue("eliminar el proyecto abierto tira lo escrito"));
        HayBoton(cut, "Eliminar").Should().BeFalse("la confirmación de eliminar no se abre mientras pregunta");

        await PulsarEnLaPreguntaAsync(cut, "Seguir editando");
        await accion.WaitAsync(Paciencia);

        DialogoAbierto(cut).Should().BeFalse("«Seguir editando» no abre la confirmación de eliminar");
        ValorDelCampo(cut, "Fecha de alta").Should().Be("2020-01-01", "y conserva lo escrito");

        var segunda = await PulsarAccionDelProyectoAsync(cut, ProyectoAbierto, "Eliminar");
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
    public async Task Aviso_eliminar_el_proyecto_abierto_con_el_alta_de_tecnico_sin_tocar_no_pregunta()
    {
        var cut = await AbrirElAltaDeTecnicoAsync();

        await ComprobarQueTerminaSinPreguntarAsync(cut, await PulsarAccionDelProyectoAsync(cut, ProyectoAbierto, "Eliminar"), "no hay nada escrito");

        HayBoton(cut, "Eliminar").Should().BeTrue("la confirmación de eliminar se abre directamente");
    }

    // ---- Cerrar el proyecto abierto desde el pie del panel

    [Fact]
    public async Task Aviso_cerrar_el_proyecto_abierto_con_el_alta_de_tecnico_a_medias_pregunta_seguir_conserva_y_descartar_abre_el_modal_de_cierre()
    {
        var cut = await AbrirElAltaDeTecnicoAsync();
        await CambiarLaFechaDeAltaAsync(cut);

        var accion = await PulsarAccionDelProyectoAsync(cut, ProyectoAbierto, "Cerrar proyecto");
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue("cerrar el proyecto abierto recarga el panel y tira lo escrito"));
        cut.FindComponents<CampoTexto>().Should().NotContain(c => c.Instance.Etiqueta == "Fecha de cierre", "el modal de cierre no se abre mientras pregunta");

        await PulsarEnLaPreguntaAsync(cut, "Seguir editando");
        await accion.WaitAsync(Paciencia);

        DialogoAbierto(cut).Should().BeFalse("«Seguir editando» no abre el modal de cierre");
        ValorDelCampo(cut, "Fecha de alta").Should().Be("2020-01-01", "y conserva lo escrito");

        var segunda = await PulsarAccionDelProyectoAsync(cut, ProyectoAbierto, "Cerrar proyecto");
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue());
        await PulsarEnLaPreguntaAsync(cut, "Salir y descartar");
        await segunda.WaitAsync(Paciencia);

        cut.FindComponents<CampoTexto>().Should().Contain(c => c.Instance.Etiqueta == "Fecha de cierre", "descartar sigue con el modal de cierre");
    }

    [Fact]
    public async Task Aviso_cerrar_el_proyecto_abierto_con_el_alta_de_tecnico_sin_tocar_no_pregunta()
    {
        var cut = await AbrirElAltaDeTecnicoAsync();

        await ComprobarQueTerminaSinPreguntarAsync(cut, await PulsarAccionDelProyectoAsync(cut, ProyectoAbierto, "Cerrar proyecto"), "no hay nada escrito");

        cut.FindComponents<CampoTexto>().Should().Contain(c => c.Instance.Etiqueta == "Fecha de cierre");
    }

    // ---- Reabrir el proyecto abierto desde el pie del panel

    [Fact]
    public async Task Aviso_reabrir_el_proyecto_abierto_con_el_alta_de_tecnico_a_medias_pregunta_seguir_conserva_y_descartar_abre_la_confirmacion()
    {
        var cut = await AbrirElAltaDeTecnicoDelCerradoAsync();
        await CambiarLaFechaDeAltaAsync(cut);

        var accion = await PulsarAccionDelProyectoAsync(cut, ProyectoCerrado, "Reabrir proyecto");
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue("reabrir el proyecto abierto recarga el panel y tira lo escrito"));
        HayBoton(cut, "Reabrir proyecto").Should().BeFalse("la confirmación de reabrir no se abre mientras pregunta");

        await PulsarEnLaPreguntaAsync(cut, "Seguir editando");
        await accion.WaitAsync(Paciencia);

        DialogoAbierto(cut).Should().BeFalse("«Seguir editando» no abre la confirmación de reabrir");
        ValorDelCampo(cut, "Fecha de alta").Should().Be("2020-01-01", "y conserva lo escrito");

        var segunda = await PulsarAccionDelProyectoAsync(cut, ProyectoCerrado, "Reabrir proyecto");
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue());
        await PulsarEnLaPreguntaAsync(cut, "Salir y descartar");
        await segunda.WaitAsync(Paciencia);

        HayBoton(cut, "Reabrir proyecto").Should().BeTrue("descartar sigue con la confirmación de reabrir");
        _mediator.Enviados.OfType<ReabrirProyectoCommand>().Should().BeEmpty("todavía no se ha confirmado");
    }

    [Fact]
    public async Task Aviso_reabrir_el_proyecto_abierto_con_el_alta_de_tecnico_sin_tocar_no_pregunta()
    {
        var cut = await AbrirElAltaDeTecnicoDelCerradoAsync();

        await ComprobarQueTerminaSinPreguntarAsync(cut, await PulsarAccionDelProyectoAsync(cut, ProyectoCerrado, "Reabrir proyecto"), "no hay nada escrito");

        HayBoton(cut, "Reabrir proyecto").Should().BeTrue("la confirmación de reabrir se abre directamente");
    }
}
