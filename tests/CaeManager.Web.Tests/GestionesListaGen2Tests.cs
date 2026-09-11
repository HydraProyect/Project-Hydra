using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Gestiones.Commands.CompletarGestion;
using CaeManager.Application.Gestiones.Commands.EliminarGestion;
using CaeManager.Application.Gestiones.Queries.ObtenerGestiones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Gestiones;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Gestiones.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Lista de Gestiones contra su mockup Gen 2 («Gestiones TALVEG.dc.html»).
/// Cubre lo que el rediseño añadió o cambió; el vacío por filtro, que ya
/// existía y se conserva, lo sigue probando <see cref="GestionesVacioPorFiltroTests"/>.
///
/// <para>
/// El cambio de más peso es de navegación: el nombre del Trabajador y el del
/// Centro abren ahora una vista rápida, y los dos 360 quedan detrás de ella.
/// </para>
///
/// <para>
/// El doble del mediador guarda las gestiones y APLICA lo que recibe: filtra
/// por <c>Estado</c> y <c>Busqueda</c>, ordena por <c>OrdenarPor</c>/<c>Descendente</c>,
/// pagina con <c>Pagina</c>/<c>TamanoPagina</c>, y los comandos cambian lo guardado. Un
/// doble que ignorase los parámetros dejaría en verde una pantalla que no los
/// envía, y uno que no mutase no distinguiría «recargó» de «volvió a pintar lo
/// mismo».
/// </para>
/// </summary>
public class GestionesListaGen2Tests : BunitContext
{
    /// <summary>QuickGrid importa su módulo JS al montarse.</summary>
    public GestionesListaGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediatorFalso : IMediator
    {
        public List<GestionListaDto> Almacen { get; } = [];
        public List<object> Enviadas { get; } = [];

        public Result ResultadoCompletar { get; set; } = Result.Exito();
        public Exception? FalloConsulta { get; set; }
        public Exception? FalloCompletar { get; set; }

        /// <summary>
        /// Si devuelve una tarea para la petición, esa es la respuesta: permite
        /// retenerla con un <see cref="TaskCompletionSource{TResult}"/> y
        /// resolverla fuera de orden.
        /// </summary>
        public Func<object, Task<object>?>? Retener { get; set; }

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);

            if (Retener?.Invoke(request) is { } retenida)
                return (TResponse)await retenida;

            // Síncrono a propósito, como los demás dobles de lista: una carga
            // que termina en otra pasada de render reasigna los manejadores de
            // las filas y el clic siguiente apunta a uno que ya no existe
            // (UnknownEventHandlerIdException, medido). Lo asíncrono de verdad
            // se prueba reteniendo con Retener.
            return (TResponse)Responder(request);
        }

        private object Responder(object request)
        {
            switch (request)
            {
                case ObtenerGestionesQuery q:
                    if (FalloConsulta is not null) throw FalloConsulta;
                    return Filtrar(q);

                case CompletarGestionCommand c:
                    if (FalloCompletar is not null) throw FalloCompletar;
                    if (!ResultadoCompletar.EsFallido)
                    {
                        var indice = Almacen.FindIndex(g => g.Id == c.Id);
                        Almacen[indice] = Almacen[indice] with
                        {
                            Estado = c.Completada ? EstadoGestion.Completada : EstadoGestion.Pendiente
                        };
                    }
                    return ResultadoCompletar;

                case EliminarGestionCommand e:
                    Almacen.RemoveAll(g => g.Id == e.Id);
                    return Result.Exito();

                default:
                    throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.");
            }
        }

        /// <summary>
        /// Filtra, ordena y pagina como <c>ObtenerGestionesQueryHandler</c>: el
        /// total es el de las coincidentes y las filas, solo las de la página
        /// pedida. Un doble que devolviera todo en orden de inserción dejaría en
        /// verde una pantalla que no propaga ni la página ni el orden.
        /// </summary>
        public ResultadoPaginado<GestionListaDto> Filtrar(ObtenerGestionesQuery q)
        {
            var coincidentes = Almacen
                .Where(g => q.Estado is null || g.Estado == q.Estado)
                .Where(g => q.Busqueda is null
                    || $"{g.TrabajadorNombre} {g.CentroNombre} {g.TipoDocumentoNombre}".Contains(q.Busqueda, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var pagina = Ordenar(coincidentes, q.OrdenarPor, q.Descendente)
                .Skip((q.Pagina - 1) * q.TamanoPagina)
                .Take(q.TamanoPagina)
                .ToList();
            return new ResultadoPaginado<GestionListaDto>(pagina, coincidentes.Count, q.Pagina, q.TamanoPagina);
        }

        /// <summary>
        /// Misma lista blanca que el handler; cualquier otro nombre (o ninguno)
        /// cae en su orden por defecto, la más reciente primero. El desempate es
        /// el orden de inserción (OrderBy es estable), no el Id como en el
        /// handler: con Ids aleatorios, los tests que señalan filas por
        /// posición cambiarían de fila entre ejecuciones.
        /// </summary>
        private static IEnumerable<GestionListaDto> Ordenar(List<GestionListaDto> filas, string? ordenarPor, bool descendente)
        {
            Func<GestionListaDto, IComparable>? clave = ordenarPor switch
            {
                nameof(GestionListaDto.TrabajadorNombre) => g => g.TrabajadorNombre,
                nameof(GestionListaDto.CentroNombre) => g => g.CentroNombre,
                nameof(GestionListaDto.TipoDocumentoNombre) => g => g.TipoDocumentoNombre,
                nameof(GestionListaDto.Estado) => g => g.Estado,
                nameof(GestionListaDto.CreadoEnUtc) => g => g.CreadoEnUtc,
                _ => null
            };

            if (clave is null)
                return filas.OrderByDescending(g => g.CreadoEnUtc);

            return descendente ? filas.OrderByDescending(clave) : filas.OrderBy(clave);
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

    private static GestionListaDto Gestion(
        string trabajador, EstadoGestion estado = EstadoGestion.Pendiente,
        string centro = "Centro Norte", string documento = "Formación PRL específica") => new(
        Guid.NewGuid(), Guid.NewGuid(), trabajador, Guid.NewGuid(), centro,
        Guid.NewGuid(), documento, estado, new DateTime(2026, 8, 14, 9, 0, 0, DateTimeKind.Utc));

    /// <param name="estado">Filtro de estado que llega por la URL (?estado=).</param>
    private IRenderedComponent<Gestiones> Renderizar(MediatorFalso mediador, string? estado = null)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();

        Services.GetRequiredService<NavigationManager>()
            .NavigateTo(estado is null ? "gestiones" : "gestiones?estado=" + estado);

        var cut = Render<Gestiones>();
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("aria-busy=\"true\""));
        return cut;
    }

    private static IElement NombreEnLaFila(IRenderedComponent<Gestiones> cut, string nombre) =>
        cut.FindAll("td .enlace-nombre-fila").First(b => b.TextContent.Trim() == nombre);

    private static IElement BotonDeLaVistaRapida(IRenderedComponent<Gestiones> cut, string texto) =>
        cut.FindAll("aside.vista-rapida-gestion button").Single(b => b.TextContent.Trim() == texto);

    private static IElement BotonDelDialogo(IRenderedComponent<Gestiones> cut, string texto) =>
        cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == texto);

    private static string EstadoEnLaVistaRapida(IRenderedComponent<Gestiones> cut) =>
        cut.Find("aside.vista-rapida-gestion .meta-vista-rapida-gestion .badge").TextContent.Trim();

    [Fact]
    public void La_cabecera_lleva_el_kicker_de_su_grupo_y_no_promete_que_las_gestiones_se_creen_solas()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Gestion("Juan Pérez Ibarra") } });

        cut.Find("header.cabecera-pagina .cabecera-pagina-kicker").TextContent.Trim().Should().Be("Operación");
        cut.Find("header.cabecera-pagina h1").TextContent.Trim().Should().Be("Gestiones");
        cut.Markup.Should().NotContain("Se crean solas",
            "CrearGestionesParaTrabajador solo se envía desde un botón: nada las crea solas");
        cut.Markup.Should().NotContain("Nueva gestión", "esta pantalla no da de alta");
    }

    [Fact]
    public async Task El_nombre_del_trabajador_abre_la_vista_rapida_con_los_datos_de_esa_fila_y_no_el_360()
    {
        var cut = Renderizar(new MediatorFalso
        {
            Almacen =
            {
                Gestion("Juan Pérez Ibarra", centro: "Centro Norte", documento: "Formación PRL específica"),
                Gestion("Nuria Salas Ortiz", EstadoGestion.Completada, centro: "Centro Logístico Sur", documento: "Reconocimiento médico"),
            }
        });

        await NombreEnLaFila(cut, "Nuria Salas Ortiz").ClickAsync(new MouseEventArgs());

        var panel = cut.Find("aside.vista-rapida-gestion");
        panel.QuerySelector(".nombre-vista-rapida-gestion")!.TextContent.Trim().Should().Be("Nuria Salas Ortiz");
        panel.TextContent.Should().Contain("Centro Logístico Sur").And.Contain("Reconocimiento médico")
            .And.Contain("creada el 14/08/2026");
        EstadoEnLaVistaRapida(cut).Should().Be("Completada");
        Services.GetRequiredService<ContextWorkspaceService>().EstaAbierto.Should().BeFalse(
            "el 360 se abre desde la vista rápida, no desde el nombre");
    }

    /// <summary>
    /// El nombre del Centro es el segundo disparador de la vista rápida (antes
    /// abría directamente el Centro 360). Desde ella, «Abrir Centro 360 →» abre
    /// el Context Workspace de ESE centro en la pestaña «informacion».
    /// </summary>
    [Fact]
    public async Task El_nombre_del_centro_abre_la_vista_rapida_de_esa_fila_y_desde_ella_el_Centro_360()
    {
        var otra = Gestion("Juan Pérez Ibarra", centro: "Centro Norte", documento: "Formación PRL específica");
        var abierta = Gestion("Nuria Salas Ortiz", EstadoGestion.Completada, centro: "Centro Logístico Sur", documento: "Reconocimiento médico");
        var cut = Renderizar(new MediatorFalso { Almacen = { otra, abierta } });
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();

        await NombreEnLaFila(cut, "Centro Logístico Sur").ClickAsync(new MouseEventArgs());

        var panel = cut.Find("aside.vista-rapida-gestion");
        panel.QuerySelector(".nombre-vista-rapida-gestion")!.TextContent.Trim().Should().Be("Nuria Salas Ortiz");
        panel.TextContent.Should().Contain("Centro Logístico Sur").And.Contain("Reconocimiento médico");
        workspace.EstaAbierto.Should().BeFalse("el nombre del centro abre la vista rápida, no el 360");

        await BotonDeLaVistaRapida(cut, "Abrir Centro 360 →").ClickAsync(new MouseEventArgs());

        var frame = workspace.FrameActual;
        frame.Should().NotBeNull();
        frame!.Tipo.Should().Be(EntidadWorkspace.Centro);
        frame.EntidadId.Should().Be(abierta.CentroId);
        frame.PestanaActiva.Should().Be("informacion");
    }

    [Fact]
    public async Task La_vista_rapida_no_inventa_origen_ni_motivo_que_la_fila_no_trae()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Gestion("Juan Pérez Ibarra") } });

        await NombreEnLaFila(cut, "Juan Pérez Ibarra").ClickAsync(new MouseEventArgs());

        var panel = cut.Find("aside.vista-rapida-gestion").TextContent;
        panel.Should().NotContain("Origen", "GestionListaDto no expone MensajeOrigenId");
        panel.Should().NotContain("Por qué existe", "GestionListaDto no expone las Notas");
        panel.Should().NotContain("Ver el documento", "una Gestion no referencia ningún Documento, solo su tipo");
    }

    [Theory]
    [InlineData("Abrir Trabajador 360 →", EntidadWorkspace.Trabajador, "informacion")]
    [InlineData("Abrir Centro 360 →", EntidadWorkspace.Centro, "informacion")]
    [InlineData("Ver la documentación del trabajador →", EntidadWorkspace.Trabajador, "documentacion")]
    public async Task Los_enlaces_de_la_vista_rapida_abren_el_360_de_esa_gestion_y_la_cierran(
        string enlace, EntidadWorkspace tipo, string pestana)
    {
        var otra = Gestion("Juan Pérez Ibarra", centro: "Centro Norte");
        var abierta = Gestion("Nuria Salas Ortiz", centro: "Centro Logístico Sur");
        var cut = Renderizar(new MediatorFalso { Almacen = { otra, abierta } });

        await NombreEnLaFila(cut, "Nuria Salas Ortiz").ClickAsync(new MouseEventArgs());
        await BotonDeLaVistaRapida(cut, enlace).ClickAsync(new MouseEventArgs());

        var frame = Services.GetRequiredService<ContextWorkspaceService>().FrameActual;
        frame.Should().NotBeNull();
        frame!.Tipo.Should().Be(tipo);
        frame.EntidadId.Should().Be(tipo == EntidadWorkspace.Centro ? abierta.CentroId : abierta.TrabajadorId);
        frame.PestanaActiva.Should().Be(pestana);
        cut.FindAll("aside.vista-rapida-gestion").Should().BeEmpty("no se apilan dos paneles laterales");
    }

    [Fact]
    public async Task Marcar_completada_desde_la_vista_rapida_manda_el_comando_de_esa_gestion_y_la_lista_recargada_lo_refleja()
    {
        var otra = Gestion("Juan Pérez Ibarra");
        var abierta = Gestion("Nuria Salas Ortiz");
        var mediador = new MediatorFalso { Almacen = { otra, abierta } };
        var cut = Renderizar(mediador);
        var consultasAntes = mediador.Enviadas.OfType<ObtenerGestionesQuery>().Count();

        await NombreEnLaFila(cut, "Nuria Salas Ortiz").ClickAsync(new MouseEventArgs());
        await BotonDeLaVistaRapida(cut, "Marcar completada").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<CompletarGestionCommand>().Should().Equal([new CompletarGestionCommand(abierta.Id, true)]);
        cut.WaitForAssertion(() => EstadoEnLaVistaRapida(cut).Should().Be("Completada"));
        BotonDeLaVistaRapida(cut, "Reabrir gestión").Should().NotBeNull("el botón pasa a la acción contraria");
        mediador.Enviadas.OfType<ObtenerGestionesQuery>().Count().Should().BeGreaterThan(consultasAntes, "tras el cambio se recarga la lista");
        cut.WaitForAssertion(() => cut.FindAll("tbody .badge").Select(b => b.TextContent.Trim())
            .Should().BeEquivalentTo(["Pendiente", "Completada"], "la fila recargada trae el estado nuevo, la otra no cambia"));
    }

    [Fact]
    public async Task Reabrir_desde_el_menu_de_la_fila_manda_el_comando_contrario()
    {
        var completada = Gestion("Nuria Salas Ortiz", EstadoGestion.Completada);
        var mediador = new MediatorFalso { Almacen = { completada } };
        var cut = Renderizar(mediador);

        await cut.Find(".menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".menu-acciones-item").Single(b => b.TextContent.Trim() == "Reabrir").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<CompletarGestionCommand>().Should().Equal([new CompletarGestionCommand(completada.Id, false)]);
        cut.WaitForAssertion(() => cut.Find("tbody .badge").TextContent.Trim().Should().Be("Pendiente"));
    }

    [Fact]
    public async Task Si_el_comando_rechaza_el_cambio_se_ensena_su_motivo_y_la_vista_rapida_no_cambia()
    {
        const string motivo = "No se encontró la gestión.";
        var mediador = new MediatorFalso
        {
            Almacen = { Gestion("Nuria Salas Ortiz") },
            ResultadoCompletar = Result.Fallo(Error.Crear("Gestion.NoEncontrada", motivo))
        };
        var cut = Renderizar(mediador);

        await NombreEnLaFila(cut, "Nuria Salas Ortiz").ClickAsync(new MouseEventArgs());
        await BotonDeLaVistaRapida(cut, "Marcar completada").ClickAsync(new MouseEventArgs());

        Services.GetRequiredService<ToastService>().Mensajes.Should()
            .ContainSingle(m => m.Mensaje == motivo && m.Tono == TonoToast.Error);
        EstadoEnLaVistaRapida(cut).Should().Be("Pendiente", "el comando no cambió nada");
    }

    [Fact]
    public async Task Si_el_comando_lanza_se_avisa_sin_romper_la_pantalla()
    {
        var mediador = new MediatorFalso
        {
            Almacen = { Gestion("Nuria Salas Ortiz") },
            FalloCompletar = new InvalidOperationException("caída de la base")
        };
        var cut = Renderizar(mediador);

        await NombreEnLaFila(cut, "Nuria Salas Ortiz").ClickAsync(new MouseEventArgs());
        await BotonDeLaVistaRapida(cut, "Marcar completada").ClickAsync(new MouseEventArgs());

        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle(m =>
            m.Mensaje == "No pudimos actualizar el estado. Intenta nuevamente en unos segundos." && m.Tono == TonoToast.Error);
        EstadoEnLaVistaRapida(cut).Should().Be("Pendiente");
        BotonDeLaVistaRapida(cut, "Marcar completada").HasAttribute("disabled").Should().BeFalse(
            "tras el fallo el botón vuelve a estar disponible para reintentar");
    }

    /// <summary>
    /// El comando de la gestión A tarda; mientras tanto se abre la B. Cuando
    /// A responde, la vista rápida enseña B y a B no le ha pasado nada.
    /// </summary>
    [Fact]
    public async Task La_respuesta_del_cambio_de_estado_de_una_gestion_no_se_aplica_a_otra_abierta_despues()
    {
        var a = Gestion("Juan Pérez Ibarra");
        var b = Gestion("Nuria Salas Ortiz");
        var respuestaDeA = new TaskCompletionSource<object>();
        var mediador = new MediatorFalso
        {
            Almacen = { a, b },
            Retener = p => p is CompletarGestionCommand ? respuestaDeA.Task : null
        };
        var cut = Renderizar(mediador);

        await NombreEnLaFila(cut, "Juan Pérez Ibarra").ClickAsync(new MouseEventArgs());
        // Sin await: el manejador espera al comando retenido, y esperarlo aquí
        // antes de soltarlo sería esperar para siempre.
        var cambioDeA = BotonDeLaVistaRapida(cut, "Marcar completada").ClickAsync(new MouseEventArgs());
        await NombreEnLaFila(cut, "Nuria Salas Ortiz").ClickAsync(new MouseEventArgs());

        await cut.InvokeAsync(() => respuestaDeA.SetResult(Result.Exito()));
        await cambioDeA;

        cut.WaitForAssertion(() => cut.Find("aside.vista-rapida-gestion .nombre-vista-rapida-gestion")
            .TextContent.Trim().Should().Be("Nuria Salas Ortiz"));
        EstadoEnLaVistaRapida(cut).Should().Be("Pendiente", "el comando era de Juan, no de Nuria");
    }

    /// <summary>
    /// Mientras viaja el cambio de A, el menú de la fila de A es un segundo
    /// disparador de lo mismo: no puede mandar otro comando. El de B sí, porque
    /// es otra gestión.
    /// </summary>
    [Fact]
    public async Task Mientras_viaja_el_cambio_de_una_gestion_no_se_manda_otro_para_ella_pero_si_para_otra()
    {
        var a = Gestion("Juan Pérez Ibarra");
        var b = Gestion("Nuria Salas Ortiz");
        var respuesta = new TaskCompletionSource<object>();
        var mediador = new MediatorFalso
        {
            Almacen = { a, b },
            Retener = p => p is CompletarGestionCommand ? respuesta.Task : null
        };
        var cut = Renderizar(mediador);

        await NombreEnLaFila(cut, "Juan Pérez Ibarra").ClickAsync(new MouseEventArgs());
        var desdeLaVistaRapida = BotonDeLaVistaRapida(cut, "Marcar completada").ClickAsync(new MouseEventArgs());

        var desdeElMenuDeA = PulsarEnElMenuDeLaFila(cut, 0, "Marcar completada");
        var desdeElMenuDeB = PulsarEnElMenuDeLaFila(cut, 1, "Marcar completada");

        mediador.Enviadas.OfType<CompletarGestionCommand>().Select(c => c.Id).Should().Equal([a.Id, b.Id],
            "uno por gestión: el segundo disparo sobre A se descarta, el de B no");

        await cut.InvokeAsync(() => respuesta.SetResult(Result.Exito()));
        await Task.WhenAll(desdeLaVistaRapida, desdeElMenuDeA, desdeElMenuDeB);
    }

    private static async Task PulsarEnElMenuDeLaFila(IRenderedComponent<Gestiones> cut, int fila, string item)
    {
        await cut.FindAll(".menu-acciones-disparador")[fila].ClickAsync(new MouseEventArgs());
        await cut.FindAll(".menu-acciones-item").Single(i => i.TextContent.Trim() == item).ClickAsync(new MouseEventArgs());
    }

    [Fact]
    public async Task Eliminar_desde_la_vista_rapida_pide_confirmacion_manda_el_comando_de_esa_gestion_y_la_cierra()
    {
        var otra = Gestion("Juan Pérez Ibarra");
        var abierta = Gestion("Nuria Salas Ortiz");
        var mediador = new MediatorFalso { Almacen = { otra, abierta } };
        var cut = Renderizar(mediador);

        await NombreEnLaFila(cut, "Nuria Salas Ortiz").ClickAsync(new MouseEventArgs());
        await BotonDeLaVistaRapida(cut, "Eliminar").ClickAsync(new MouseEventArgs());

        cut.Find("[role=dialog]").TextContent.Should().Contain("¿Eliminar esta gestión?");
        mediador.Enviadas.OfType<EliminarGestionCommand>().Should().BeEmpty("abrir el diálogo no borra nada");

        await BotonDelDialogo(cut, "Eliminar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EliminarGestionCommand>().Should().Equal([new EliminarGestionCommand(abierta.Id)]);
        cut.WaitForAssertion(() => cut.FindAll("aside.vista-rapida-gestion").Should().BeEmpty(
            "una vista rápida sobre la gestión borrada enseñaría algo que ya no está"));
        cut.WaitForAssertion(() => cut.FindAll("td .enlace-nombre-fila").Select(e => e.TextContent.Trim())
            .Should().NotContain("Nuria Salas Ortiz").And.Contain("Juan Pérez Ibarra"));
    }

    [Fact]
    public async Task Eliminar_otra_gestion_desde_el_menu_no_cierra_la_vista_rapida_abierta()
    {
        var abierta = Gestion("Juan Pérez Ibarra");
        var borrada = Gestion("Nuria Salas Ortiz");
        var mediador = new MediatorFalso { Almacen = { abierta, borrada } };
        var cut = Renderizar(mediador);

        await NombreEnLaFila(cut, "Juan Pérez Ibarra").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".menu-acciones-disparador")[1].ClickAsync(new MouseEventArgs());
        await cut.FindAll(".menu-acciones-item").Single(b => b.TextContent.Trim() == "Eliminar").ClickAsync(new MouseEventArgs());
        await BotonDelDialogo(cut, "Eliminar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EliminarGestionCommand>().Should().Equal([new EliminarGestionCommand(borrada.Id)]);
        cut.WaitForAssertion(() => cut.Find("aside.vista-rapida-gestion .nombre-vista-rapida-gestion")
            .TextContent.Trim().Should().Be("Juan Pérez Ibarra"));
    }

    /// <summary>
    /// La consulta del filtro anterior tarda; mientras tanto se cambia a
    /// «Completadas», que responde en seguida sin nada. Cuando la vieja llega,
    /// no puede pisar el total: la pantalla sigue diciendo que ninguna coincide.
    /// </summary>
    [Fact]
    public async Task Una_respuesta_lenta_del_filtro_anterior_no_pisa_el_resultado_del_nuevo()
    {
        var pendientes = new[] { Gestion("Juan Pérez Ibarra"), Gestion("Nuria Salas Ortiz"), Gestion("Marco Vila Cuenca") };
        var respuestaVieja = new TaskCompletionSource<object>();
        var retenida = false;
        var mediador = new MediatorFalso();
        mediador.Almacen.AddRange(pendientes);
        mediador.Retener = p =>
        {
            if (retenida || p is not ObtenerGestionesQuery { Estado: EstadoGestion.Pendiente }) return null;
            retenida = true;
            return respuestaVieja.Task;
        };

        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.GetRequiredService<NavigationManager>().NavigateTo("gestiones?estado=Pendiente");
        var cut = Render<Gestiones>();

        await cut.Find(".barra-filtros select").ChangeAsync(new ChangeEventArgs { Value = nameof(EstadoGestion.Completada) });
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ninguna gestión con estos filtros"));

        await cut.InvokeAsync(() => respuestaVieja.SetResult(
            mediador.Filtrar(new ObtenerGestionesQuery(null, EstadoGestion.Pendiente, null))));

        // Cualquier repintado posterior de la página enseña el estado que dejó
        // la respuesta vieja; se fuerza uno para no depender de cuál llegue.
        cut.Render();

        cut.Markup.Should().Contain("Ninguna gestión con estos filtros",
            "la respuesta vieja era de «Pendientes», no de la pregunta vigente");
        cut.FindAll(".conteo-gestiones").Should().BeEmpty("no hay coincidencias con el filtro vigente");
    }

    [Fact]
    public async Task Quitar_el_chip_de_estado_lo_quita_tambien_de_la_url_y_de_la_consulta()
    {
        var mediador = new MediatorFalso { Almacen = { Gestion("Juan Pérez Ibarra"), Gestion("Iker Zubiaga Mena", EstadoGestion.Completada) } };
        var cut = Renderizar(mediador, estado: nameof(EstadoGestion.Completada));
        var navegacion = Services.GetRequiredService<NavigationManager>();

        cut.Find(".chip-filtro").TextContent.Should().Contain("Estado: completadas");
        navegacion.Uri.Should().Contain("estado=Completada", "es el punto de partida de este caso");

        await cut.Find(".chip-filtro-quitar").ClickAsync(new MouseEventArgs());

        navegacion.Uri.Should().NotContain("estado=");
        mediador.Enviadas.OfType<ObtenerGestionesQuery>().Last().Estado.Should().BeNull();
        cut.WaitForAssertion(() => cut.FindAll(".chip-filtro").Should().BeEmpty());
        cut.WaitForAssertion(() => cut.Find(".conteo-gestiones").TextContent.Trim().Should().Be("2 gestiones"));
    }

    /// <summary>
    /// El doble filtra por <c>Estado</c>: de tres, una está completada. Si la
    /// pantalla no enviara el ?estado= de la URL, el conteo diría 3.
    /// </summary>
    [Fact]
    public void El_conteo_dice_cuantas_coinciden_y_con_filtros_lo_dice()
    {
        var mediador = new MediatorFalso
        {
            Almacen = { Gestion("Juan Pérez Ibarra"), Gestion("Nuria Salas Ortiz"), Gestion("Iker Zubiaga Mena", EstadoGestion.Completada) }
        };
        var cut = Renderizar(mediador, estado: nameof(EstadoGestion.Completada));

        mediador.Enviadas.OfType<ObtenerGestionesQuery>().Last().Estado.Should().Be(EstadoGestion.Completada);
        cut.Find(".conteo-gestiones").TextContent.Trim().Should().Be("1 gestión con estos filtros");
    }

    [Fact]
    public void Sin_filtros_el_conteo_no_habla_de_ningun_filtro()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Gestion("Juan Pérez Ibarra"), Gestion("Nuria Salas Ortiz") } });

        cut.Find(".conteo-gestiones").TextContent.Trim().Should().Be("2 gestiones");
        cut.FindAll(".chip-filtro").Should().BeEmpty();
    }

    private static IElement CabeceraOrdenable(IRenderedComponent<Gestiones> cut, string titulo) =>
        cut.FindAll("thead th").Single(th => th.TextContent.Trim() == titulo).QuerySelector("button")!;

    /// <summary>Texto del botón de nombre en la posición <paramref name="columna"/> (0 Trabajador, 1 Centro) de cada fila.</summary>
    private static List<string> ColumnaDeLasFilas(IRenderedComponent<Gestiones> cut, int columna) =>
        cut.FindAll("tbody tr")
            .Select(tr => tr.QuerySelectorAll(".enlace-nombre-fila"))
            .Where(botones => botones.Length > columna)
            .Select(botones => botones[columna].TextContent.Trim())
            .ToList();

    /// <summary>
    /// El doble ordena según lo que recibe: si la pantalla no enviara el
    /// <c>OrdenarPor</c>/<c>Descendente</c> de la cabecera, las filas llegarían
    /// en el orden por defecto (el de inserción, con la misma fecha), que no es
    /// ni el ascendente ni el descendente por centro.
    /// </summary>
    [Fact]
    public async Task Pulsar_la_cabecera_Centro_ordena_la_consulta_por_centro_y_la_segunda_vez_al_reves()
    {
        var mediador = new MediatorFalso
        {
            Almacen =
            {
                Gestion("Juan Pérez Ibarra", centro: "Centro Norte"),
                Gestion("Nuria Salas Ortiz", centro: "Centro Este"),
                Gestion("Iker Zubiaga Mena", centro: "Centro Sur"),
            }
        };
        var cut = Renderizar(mediador);
        ColumnaDeLasFilas(cut, 1).Should().Equal(["Centro Norte", "Centro Este", "Centro Sur"],
            "punto de partida: el orden por defecto no coincide con ninguno de los dos que se piden");

        await CabeceraOrdenable(cut, "Centro").ClickAsync(new MouseEventArgs());

        var ascendente = mediador.Enviadas.OfType<ObtenerGestionesQuery>().Last();
        ascendente.OrdenarPor.Should().Be(nameof(GestionListaDto.CentroNombre));
        ascendente.Descendente.Should().BeFalse();
        cut.WaitForAssertion(() => ColumnaDeLasFilas(cut, 1).Should().Equal(["Centro Este", "Centro Norte", "Centro Sur"]));

        await CabeceraOrdenable(cut, "Centro").ClickAsync(new MouseEventArgs());

        var descendente = mediador.Enviadas.OfType<ObtenerGestionesQuery>().Last();
        descendente.OrdenarPor.Should().Be(nameof(GestionListaDto.CentroNombre));
        descendente.Descendente.Should().BeTrue("la segunda pulsación invierte el orden");
        cut.WaitForAssertion(() => ColumnaDeLasFilas(cut, 1).Should().Equal(["Centro Sur", "Centro Norte", "Centro Este"]));
    }

    /// <summary>
    /// 25 gestiones con fechas distintas: la página 1 son las 20 más recientes
    /// y la 2, las cinco más antiguas. El doble pagina con lo que recibe; si la
    /// pantalla mandara siempre la página 1, la segunda repetiría la primera.
    /// </summary>
    [Fact]
    public async Task Pasar_a_la_pagina_siguiente_pide_la_pagina_2_y_pinta_sus_filas()
    {
        var mediador = new MediatorFalso();
        for (var i = 1; i <= 25; i++)
        {
            mediador.Almacen.Add(Gestion($"Trabajador {i:00}") with
            {
                CreadoEnUtc = new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc).AddDays(i)
            });
        }
        var cut = Renderizar(mediador);
        ColumnaDeLasFilas(cut, 0).Should().HaveCount(20).And.StartWith("Trabajador 25");
        cut.Find(".paginador-texto").TextContent.Should().Contain("Página 1 de 2");

        await cut.FindAll(".paginador-simple button").Single(b => b.TextContent.Contains("Siguiente")).ClickAsync(new MouseEventArgs());

        var consulta = mediador.Enviadas.OfType<ObtenerGestionesQuery>().Last();
        consulta.Pagina.Should().Be(2);
        consulta.TamanoPagina.Should().Be(20);
        cut.WaitForAssertion(() => ColumnaDeLasFilas(cut, 0).Should().Equal(
            ["Trabajador 05", "Trabajador 04", "Trabajador 03", "Trabajador 02", "Trabajador 01"]));
        cut.Find(".paginador-texto").TextContent.Should().Contain("Página 2 de 2").And.Contain("25 gestión(es)");
    }

    [Fact]
    public void Sin_gestiones_ofrece_ir_a_Mi_trabajo()
    {
        var cut = Renderizar(new MediatorFalso());

        var enlace = cut.FindAll(".estado-vacio a").Single();
        enlace.TextContent.Trim().Should().Be("Ir a Mi trabajo →");
        enlace.GetAttribute("href").Should().Be("bandeja", "Mi trabajo es /bandeja en el menú");
    }

    [Fact]
    public async Task Si_la_consulta_falla_se_ofrece_reintentar_y_reintentar_vuelve_a_pedir_la_lista()
    {
        var mediador = new MediatorFalso
        {
            Almacen = { Gestion("Juan Pérez Ibarra") },
            FalloConsulta = new InvalidOperationException("caída de la base")
        };
        var cut = Renderizar(mediador);
        cut.Markup.Should().Contain("No pudimos cargar las gestiones");

        mediador.FalloConsulta = null;
        await cut.FindAll(".estado-vacio button").Single(b => b.TextContent.Trim() == "Reintentar").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("No pudimos cargar las gestiones"));
        cut.WaitForAssertion(() => cut.FindAll("td .enlace-nombre-fila").Select(e => e.TextContent.Trim())
            .Should().Contain("Juan Pérez Ibarra"));
    }
}
