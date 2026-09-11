using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Incidencias.Commands.EditarIncidencia;
using CaeManager.Application.Incidencias.Commands.EliminarIncidencia;
using CaeManager.Application.Incidencias.Queries.ObtenerIncidenciaPorId;
using CaeManager.Application.Incidencias.Queries.ObtenerIncidencias;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Common;
using CaeManager.Domain.Incidencias;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Incidencias.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Pantalla Incidencias contra su mockup Gen 2 (Incidencias TALVEG.dc.html).
/// Cada prueba mira el EFECTO: el comando que sale con su id y sus datos, o
/// lo que la pantalla termina diciendo tras cargar — no el camino.
/// </summary>
public class IncidenciasGen2Tests : BunitContext
{
    /// <summary>La página importa ./js/atajos-lista.js y el Drawer ./js/dialogo-foco.js; quedan fuera de lo que se observa.</summary>
    public IncidenciasGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly Guid IdAlfa = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid IdBeta = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid VersionAlfa = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid VersionBeta = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");

    private static IncidenciaListaDto Fila(Guid id, string centro, bool resuelta = false) =>
        new(id, Guid.NewGuid(), centro, null, null, TipoIncidencia.Accidente, GravedadIncidencia.Grave,
            new DateOnly(2026, 9, 2), resuelta);

    private static IncidenciaDetalleDto Detalle(Guid id, string centro, Guid version, string descripcion) =>
        new(id, Guid.NewGuid(), centro, null, TipoIncidencia.Incumplimiento, GravedadIncidencia.MuyGrave,
            new DateOnly(2026, 9, 1), descripcion, false, version);

    /// <summary>
    /// Doble del mediador que REGISTRA cada petición con sus parámetros (un
    /// doble que los ignorase daría verde a una pantalla que no los envía) y
    /// deja controlar CUÁNDO responde cada una: una consulta diferida queda
    /// pendiente de su propio <see cref="TaskCompletionSource{T}"/> y la prueba
    /// decide el orden en que llegan las respuestas.
    /// </summary>
    private sealed class MediadorControlado : IMediator
    {
        public List<object> Peticiones { get; } = [];

        public List<(ObtenerIncidenciasQuery Consulta, TaskCompletionSource<ResultadoPaginado<IncidenciaListaDto>> Respuesta)> CargasPendientes { get; } = [];

        public Dictionary<Guid, TaskCompletionSource<IncidenciaDetalleDto?>> DetallesPendientes { get; } = [];

        public IReadOnlyList<IncidenciaListaDto> Filas { get; set; } = [];

        /// <summary>Retiene TODAS las cargas de la lista.</summary>
        public bool CargasDiferidas { get; set; }

        /// <summary>
        /// Retiene solo las cargas que cumplen esto; el resto responde al
        /// momento. Existe porque un cambio de filtro puede lanzar más de una
        /// carga (la recarga pasa por SetCurrentPageIndexAsync y por
        /// RefreshDataAsync): una versión anterior de esta prueba resolvía «las
        /// cargas que hay ahora», apareció otra después, y el manejador del
        /// cambio no terminó nunca — vstest la abortó por cuelgue.
        /// </summary>
        public Func<ObtenerIncidenciasQuery, bool>? DiferirCarga { get; set; }

        public bool DetallesDiferidos { get; set; }

        public bool FallarCargas { get; set; }

        public Dictionary<Guid, IncidenciaDetalleDto> Detalles { get; } = [];

        public Result RespuestaEditar { get; set; } = Result.Exito();

        public IEnumerable<ObtenerIncidenciasQuery> Consultas => Peticiones.OfType<ObtenerIncidenciasQuery>();

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Peticiones.Add(request);

            switch (request)
            {
                case ObtenerIncidenciasQuery q:
                    if (FallarCargas)
                        return Task.FromException<TResponse>(new InvalidOperationException("Fallo de carga simulado."));

                    if (CargasDiferidas || DiferirCarga?.Invoke(q) == true)
                    {
                        // La tarea tal cual y con continuaciones SÍNCRONAS, sin
                        // ContinueWith: completarla dentro de cut.InvokeAsync
                        // ejecuta allí mismo, en el dispatcher, la continuación
                        // de la página, así que cuando ResolverAsync vuelve la
                        // respuesta YA se procesó y la aserción siguiente mira
                        // su efecto. Con el salto al pool que había antes, la
                        // aserción podía correr antes que la respuesta: quitar
                        // la guarda de generación (mutación M1) salía en verde.
                        var tcs = new TaskCompletionSource<ResultadoPaginado<IncidenciaListaDto>>();
                        CargasPendientes.Add((q, tcs));
                        return (Task<TResponse>)(object)tcs.Task;
                    }

                    return Task.FromResult((TResponse)(object)new ResultadoPaginado<IncidenciaListaDto>(Filas, Filas.Count, q.Pagina, q.TamanoPagina));

                case ObtenerIncidenciaPorIdQuery q:
                    if (DetallesDiferidos)
                    {
                        // Mismo motivo que las cargas diferidas de arriba.
                        var tcs = new TaskCompletionSource<IncidenciaDetalleDto?>();
                        DetallesPendientes[q.Id] = tcs;
                        return (Task<TResponse>)(object)tcs.Task;
                    }

                    return Task.FromResult((TResponse)(object)Detalles[q.Id]);

                case ObtenerCentrosParaSelectorQuery:
                    return Task.FromResult((TResponse)(object)Array.Empty<CentroSelectorDto>());

                case ObtenerTrabajadoresParaSelectorQuery:
                    return Task.FromResult((TResponse)(object)Array.Empty<TrabajadorSelectorDto>());

                case EditarIncidenciaCommand:
                    return Task.FromResult((TResponse)(object)RespuestaEditar);

                case EliminarIncidenciaCommand:
                    return Task.FromResult((TResponse)(object)Result.Exito());

                default:
                    throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.");
            }
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

    private sealed class UsuarioActualFalso : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private IRenderedComponent<Incidencias> Renderizar(MediadorControlado mediador, string? estado = null)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();

        Services.GetRequiredService<NavigationManager>()
            .NavigateTo(estado is null ? "incidencias" : "incidencias?estado=" + Uri.EscapeDataString(estado));

        return Render<Incidencias>();
    }

    private static Task ResolverAsync(
        IRenderedComponent<Incidencias> cut,
        TaskCompletionSource<ResultadoPaginado<IncidenciaListaDto>> respuesta,
        params IncidenciaListaDto[] filas) =>
        cut.InvokeAsync(() => respuesta.TrySetResult(new ResultadoPaginado<IncidenciaListaDto>(filas, filas.Length, 1, 20)));

    /// <summary>El select del filtro de estado: el único de la página con la opción «Todas».</summary>
    private static AngleSharp.Dom.IElement SelectDeEstado(IRenderedComponent<Incidencias> cut) =>
        cut.FindAll("select").Single(s => s.QuerySelector("option[value='']")?.TextContent == "Todas");

    [Fact]
    public async Task Una_carga_lenta_de_un_filtro_anterior_no_pisa_la_respuesta_del_filtro_vigente()
    {
        // Solo se retienen las cargas de «Todas» (la primera, sin filtro); las
        // de «Sin resolver» responden al momento. La del filtro vigente llega
        // así ANTES que la ya superada, sin que la prueba tenga que adivinar
        // cuántas cargas lanza QuickGrid por cambio (ver DiferirCarga).
        var mediador = new MediadorControlado { DiferirCarga = q => q.Resuelta is null };
        var cut = Renderizar(mediador);

        cut.WaitForAssertion(() => mediador.CargasPendientes.Should().NotBeEmpty());

        await SelectDeEstado(cut).ChangeAsync(new ChangeEventArgs { Value = "SinResolver" });

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ninguna incidencia con estos filtros"));
        mediador.Consultas.Should().Contain(q => q.Resuelta == false, "la carga vigente salió con el filtro puesto");

        // Y DESPUÉS llega la de «Todas», ya superada, con una incidencia.
        foreach (var carga in mediador.CargasPendientes.ToList())
            await ResolverAsync(cut, carga.Respuesta, Fila(IdAlfa, "Centro Alfa"));

        cut.Markup.Should().Contain("Ninguna incidencia con estos filtros",
            "la respuesta de un filtro que ya no está puesto no puede decidir qué estado vacío se pinta");
        cut.Markup.Should().NotContain("1 incidencia",
            "el recuento es el de «Sin resolver», no el de la carga de «Todas» que llegó tarde");
    }

    [Fact]
    public void Mientras_llega_la_primera_carga_se_pinta_el_esqueleto_y_ningun_estado_vacio()
    {
        var mediador = new MediadorControlado { CargasDiferidas = true };
        var cut = Renderizar(mediador);

        cut.WaitForAssertion(() => mediador.CargasPendientes.Should().NotBeEmpty());

        cut.FindAll(".esqueleto-lista").Should().ContainSingle("la carga sigue pendiente");
        cut.Markup.Should().NotContain("Todavía no hay incidencias",
            "sin respuesta todavía, la pantalla no sabe si hay incidencias o no");
    }

    [Fact]
    public async Task Abrir_editar_en_dos_filas_seguidas_deja_el_formulario_de_la_ultima_y_guarda_sobre_ella()
    {
        var mediador = new MediadorControlado
        {
            Filas = [Fila(IdAlfa, "Centro Alfa"), Fila(IdBeta, "Centro Beta")],
            DetallesDiferidos = true
        };
        var cut = Renderizar(mediador);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Centro Beta"));

        // «Editar» en Alfa: su detalle queda pendiente. El clic no se espera,
        // porque su manejador está a mitad de un await que decide la prueba.
        await cut.FindAll(".menu-acciones-disparador")[0].ClickAsync(new());
        var editarAlfa = cut.FindAll(".menu-acciones-item").Single(b => b.TextContent.Trim() == "Editar");
        var clicAlfa = editarAlfa.ClickAsync(new());
        cut.WaitForAssertion(() => mediador.DetallesPendientes.Should().ContainKey(IdAlfa));

        // «Editar» en Beta, con el de Alfa todavía en vuelo.
        await cut.FindAll(".menu-acciones-disparador")[1].ClickAsync(new());
        var editarBeta = cut.FindAll(".menu-acciones-item").Where(b => b.TextContent.Trim() == "Editar").Last();
        var clicBeta = editarBeta.ClickAsync(new());
        cut.WaitForAssertion(() => mediador.DetallesPendientes.Should().ContainKey(IdBeta));

        // Llega primero Beta y DESPUÉS Alfa, la respuesta ya superada.
        await cut.InvokeAsync(() => mediador.DetallesPendientes[IdBeta].SetResult(Detalle(IdBeta, "Centro Beta", VersionBeta, "Andamio sin barandilla")));
        await clicBeta;
        await cut.InvokeAsync(() => mediador.DetallesPendientes[IdAlfa].SetResult(Detalle(IdAlfa, "Centro Alfa", VersionAlfa, "Caída en la nave")));
        await clicAlfa;

        cut.Find(".drawer-panel .texto-vacio-seccion").TextContent.Should().Be("Centro Beta",
            "el formulario a la vista es el de la última fila en la que se pulsó «Editar»");

        await cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new());

        var comando = mediador.Peticiones.OfType<EditarIncidenciaCommand>().Should().ContainSingle().Subject;
        comando.Id.Should().Be(IdBeta, "guardar tiene que ir contra la incidencia que el formulario enseña");
        comando.Version.Should().Be(VersionBeta);
        comando.Descripcion.Should().Be("Andamio sin barandilla");
        comando.Gravedad.Should().Be(GravedadIncidencia.MuyGrave);
    }

    [Fact]
    public async Task Reintentar_tras_un_error_de_carga_vuelve_a_pedir_la_lista_y_la_pinta()
    {
        var mediador = new MediadorControlado { FallarCargas = true, Filas = [Fila(IdAlfa, "Centro Alfa")] };
        var cut = Renderizar(mediador);

        cut.WaitForAssertion(() => cut.Find("[role='alert']").TextContent.Should().Contain("No pudimos cargar las incidencias"));

        mediador.FallarCargas = false;
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Reintentar").ClickAsync(new());

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Centro Alfa"));
        cut.Markup.Should().NotContain("No pudimos cargar las incidencias");
    }

    [Fact]
    public async Task El_chip_de_estado_quita_el_filtro_de_la_lista_y_de_la_url()
    {
        var mediador = new MediadorControlado { Filas = [Fila(IdAlfa, "Centro Alfa")] };
        var cut = Renderizar(mediador, estado: "SinResolver");

        cut.WaitForAssertion(() => cut.Find(".chip-filtro").TextContent.Should().Contain("Estado: sin resolver"));
        mediador.Consultas.Last().Resuelta.Should().Be(false, "punto de partida: la consulta sale filtrada por la URL");

        await cut.Find("button[aria-label='Quitar el filtro de estado']").ClickAsync(new());

        cut.WaitForAssertion(() => mediador.Consultas.Last().Resuelta.Should().BeNull());
        Services.GetRequiredService<NavigationManager>().Uri.Should().NotContain("estado=",
            "si el estado se quedara en la URL, OnParametersSet lo devolvería en la siguiente navegación");
        cut.FindAll(".chip-filtro").Should().BeEmpty();
    }

    [Fact]
    public void El_recuento_solo_dice_con_el_filtro_actual_cuando_hay_filtro()
    {
        var sinFiltro = Renderizar(new MediadorControlado { Filas = [Fila(IdAlfa, "Centro Alfa")] });

        sinFiltro.WaitForAssertion(() => sinFiltro.Find(".recuento-incidencias").TextContent.Trim().Should().Be("1 incidencia"));
    }

    [Fact]
    public void Con_filtro_el_recuento_lo_dice()
    {
        var cut = Renderizar(
            new MediadorControlado { Filas = [Fila(IdAlfa, "Centro Alfa"), Fila(IdBeta, "Centro Beta")] },
            estado: "SinResolver");

        cut.WaitForAssertion(() => cut.Find(".recuento-incidencias").TextContent.Trim()
            .Should().Be("2 incidencias con el filtro actual"));
    }

    [Fact]
    public async Task La_busqueda_viaja_en_la_consulta()
    {
        var mediador = new MediadorControlado { Filas = [Fila(IdAlfa, "Centro Alfa")] };
        var cut = Renderizar(mediador);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Centro Alfa"));

        await cut.Find("input[placeholder^='Buscar por centro']").InputAsync(new ChangeEventArgs { Value = "andamio" });

        cut.WaitForAssertion(() => mediador.Consultas.Last().Busqueda.Should().Be("andamio"), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Un_conflicto_de_version_se_explica_con_el_texto_de_la_incidencia()
    {
        var mediador = new MediadorControlado
        {
            Filas = [Fila(IdAlfa, "Centro Alfa")],
            RespuestaEditar = Result.Fallo(Error.Crear(ConcurrenciaOptimista.CodigoConflicto, "Otra persona modificó esta incidencia mientras lo editabas."))
        };
        mediador.Detalles[IdAlfa] = Detalle(IdAlfa, "Centro Alfa", VersionAlfa, "Caída en la nave");
        var cut = Renderizar(mediador);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Centro Alfa"));

        await cut.Find(".menu-acciones-disparador").ClickAsync(new());
        await cut.FindAll(".menu-acciones-item").Single(b => b.TextContent.Trim() == "Editar").ClickAsync(new());
        await cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new());

        mediador.Peticiones.OfType<EditarIncidenciaCommand>().Should().ContainSingle()
            .Which.Version.Should().Be(VersionAlfa, "el conflicto se detecta porque viaja la versión leída al abrir");
        cut.Find(".alerta-formulario").TextContent.Should().Be(
            "Otra persona guardó esta incidencia mientras la tenías abierta. Vuelve a abrirla para no pisar sus cambios.");
    }

    [Fact]
    public async Task El_atajo_x_marca_la_fila_a_la_vista_y_no_en_una_seleccion_invisible()
    {
        var mediador = new MediadorControlado { Filas = [Fila(IdAlfa, "Centro Alfa")] };
        var cut = Renderizar(mediador);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Centro Alfa"));
        cut.FindAll("input[type='checkbox']").Should().BeEmpty("sin «Selección múltiple» no hay casillas");

        var atajos = cut.FindComponent<AtajosListaTeclado>();
        await cut.InvokeAsync(() => atajos.Instance.RecibirAtajo("j"));
        await cut.InvokeAsync(() => atajos.Instance.RecibirAtajo("x"));

        cut.Find("input[aria-label^='Seleccionar la incidencia de Centro Alfa']").HasAttribute("checked").Should().BeTrue(
            "la fila marcada con x tiene que verse marcada, no quedar en una selección que la tabla no enseña");
        cut.Find(".barra-acciones-lote-cantidad").TextContent.Should().Contain("1 seleccionado");
    }

    [Fact]
    public async Task Eliminar_una_incidencia_confirma_y_envia_el_comando_con_su_id()
    {
        var mediador = new MediadorControlado { Filas = [Fila(IdAlfa, "Centro Alfa"), Fila(IdBeta, "Centro Beta")] };
        var cut = Renderizar(mediador);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Centro Beta"));

        await cut.FindAll(".menu-acciones-disparador")[1].ClickAsync(new());
        await cut.FindAll(".menu-acciones-item").Single(b => b.TextContent.Trim() == "Eliminar").ClickAsync(new());

        mediador.Peticiones.OfType<EliminarIncidenciaCommand>().Should().BeEmpty("abrir el diálogo no borra nada");
        cut.Find(".modal-contenido").TextContent.Should().Contain("¿Eliminar la incidencia de Centro Beta?");

        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Eliminar").ClickAsync(new());

        mediador.Peticiones.OfType<EliminarIncidenciaCommand>().Should().ContainSingle()
            .Which.Id.Should().Be(IdBeta);
    }
}
