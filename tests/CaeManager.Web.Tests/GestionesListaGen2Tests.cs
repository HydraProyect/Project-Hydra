using CaeManager.Infrastructure.Identity;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Gestiones.Commands.CompletarGestion;
using CaeManager.Application.Gestiones.Commands.EliminarGestion;
using CaeManager.Application.Gestiones.Commands.RestaurarGestion;
using CaeManager.Application.Gestiones.Queries.ObtenerGestiones;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
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
using Microsoft.JSInterop;

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
public partial class GestionesListaGen2Tests : BunitContext
{
    /// <summary>QuickGrid importa su módulo JS al montarse.</summary>
    public GestionesListaGen2Tests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        this.ConRolDeEscritura();
    }

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

                case RestaurarGestionCommand:
                    return Result.Exito();

                // El estado vacío sin filtros pregunta cuántos Tenants hay para decidir adónde
                // lleva «Ir a Mi trabajo»: uno solo, /bandeja (GestionesVacioEnlaceMiTrabajoTests
                // cubre el caso de varios).
                case ObtenerClientesAutorizadosQuery:
                    return (IReadOnlyList<ClienteAutorizadoDto>)[new(Guid.NewGuid(), "Organización de prueba", EsOrigen: true)];

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
                    || TextoDeBusqueda.Contiene($"{g.TrabajadorNombre} {g.CentroNombre} {g.TipoDocumentoNombre}", q.Busqueda))
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

            if (ordenarPor == nameof(GestionListaDto.Estado))
                return descendente
                    ? filas.OrderByDescending(g => g.Estado).ThenByDescending(g => g.CreadoEnUtc)
                    : filas.OrderBy(g => g.Estado).ThenByDescending(g => g.CreadoEnUtc);

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
    private IRenderedComponent<Gestiones> Renderizar(MediatorFalso mediador, string? estado = null, string? url = null)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();

        Services.GetRequiredService<NavigationManager>()
            .NavigateTo(url ?? (estado is null ? "gestiones" : "gestiones?estado=" + estado));

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
    public void La_cabecera_lleva_total_medido_sin_kicker_alta_ni_seleccion()
    {
        var m = new MediatorFalso { Almacen = { Gestion("Juan Pérez Ibarra") } };
        var cut = Renderizar(m);
        m.Enviadas.OfType<ObtenerGestionesQuery>().Should().NotBeEmpty();
        cut.FindAll("tbody tr:has(.gestion-trabajador-centro)").Should().ContainSingle();
        cut.FindAll("header.cabecera-pagina .cabecera-pagina-kicker").Should().BeEmpty();
        cut.Find("header.cabecera-pagina h1").TextContent.Trim().Should().Be("Gestiones");
        cut.Find("header.cabecera-pagina .cabecera-listado-contador").TextContent.Trim().Should().Be("1");
        cut.FindAll("header.cabecera-pagina button[aria-label='Selección múltiple']").Should().BeEmpty();
        cut.Markup.Should().NotContain("Se crean solas");
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
        // La celda de estado (EstadoFila): lo pendiente lleva pastilla; lo completado no pide acción y va sin ella.
        cut.WaitForAssertion(() => cut.FindAll("tbody [data-pieza=estado-correcto]").Select(b => b.TextContent.Trim())
            .Should().Equal(["Completada"], "la fila recargada trae el estado nuevo"));
        cut.FindAll("tbody .badge").Select(b => b.TextContent.Trim()).Should().Equal(["Pendiente"], "la otra no cambia");
    }

    [Fact]
    public async Task Consulta_abre_la_vista_rapida_sin_que_se_le_ofrezca_completar_ni_eliminar()
    {
        // Completar y eliminar son ICommand que AutorizacionEscrituraBehavior deniega a
        // Consulta: ofrecerlos en la vista rápida era enseñar un botón que siempre falla.
        this.ConRolDeEscritura(Roles.Consulta);
        var cut = Renderizar(new MediatorFalso { Almacen = { Gestion("Nuria Salas Ortiz") } });

        await NombreEnLaFila(cut, "Nuria Salas Ortiz").ClickAsync(new MouseEventArgs());

        cut.FindAll("aside.vista-rapida-gestion button").Select(b => b.TextContent.Trim())
            .Should().NotContain(["Marcar completada", "Reabrir gestión", "Eliminar"])
            .And.Contain("Abrir Centro 360 →", "la navegación a los 360 sí es de lectura");
    }

    [Fact]
    public async Task Reabrir_desde_el_boton_rapido_de_la_fila_manda_el_comando_contrario()
    {
        var completada = Gestion("Nuria Salas Ortiz", EstadoGestion.Completada);
        var mediador = new MediatorFalso { Almacen = { completada } };
        var cut = Renderizar(mediador);

        FilaGestionFase1(cut, completada.TrabajadorNombre).QuerySelectorAll("button.gestion-completar")
            .Should().BeEmpty("una Gestión completada no se completa otra vez");
        await BotonRapidoDeLaFila(cut, completada.TrabajadorNombre, "Reabrir").ClickAsync(new MouseEventArgs());

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
    /// Mientras viaja el cambio de A, un segundo clic en su botón rápido y el botón del pie de su
    /// vista rápida son más disparadores de lo mismo: no pueden mandar otro comando. Los dos se
    /// pintan deshabilitados, pero un doble clic llega antes que el repintado: bUnit despacha el
    /// clic igualmente, y es la guarda de CambiarEstadoAsync la que lo para. El de B sí manda el
    /// suyo, porque es otra gestión.
    /// </summary>
    [Fact]
    public async Task Mientras_viaja_el_cambio_de_una_gestion_no_se_manda_otro_para_ella_pero_si_para_otra()
    {
        var a = Gestion("Juan Pérez Ibarra");
        var b = Gestion("Nuria Salas Ortiz");
        var respuesta = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mediador = new MediatorFalso
        {
            Almacen = { a, b },
            Retener = p => p is CompletarGestionCommand ? respuesta.Task : null
        };
        var cut = Renderizar(mediador);
        var tareas = new List<Task>();
        var huboError = false;
        try
        {
            tareas.Add(FilaGestionFase1(cut, a.TrabajadorNombre).QuerySelector("button.gestion-completar")!.ClickAsync(new MouseEventArgs()));
            cut.WaitForAssertion(() => mediador.Enviadas.OfType<CompletarGestionCommand>().Should().ContainSingle());
            await NombreEnLaFila(cut, a.TrabajadorNombre).ClickAsync(new MouseEventArgs());
            tareas.Add(BotonDeLaVistaRapida(cut, "Marcar completada").ClickAsync(new MouseEventArgs()));
            tareas.Add(BotonRapidoDeLaFila(cut, a.TrabajadorNombre, "Completar").ClickAsync(new MouseEventArgs()));
            tareas.Add(BotonRapidoDeLaFila(cut, b.TrabajadorNombre, "Completar").ClickAsync(new MouseEventArgs()));
            cut.WaitForAssertion(() => mediador.Enviadas.OfType<CompletarGestionCommand>().Select(c => c.Id).Should().Equal(a.Id, b.Id));
            mediador.Enviadas.OfType<CompletarGestionCommand>().Should().OnlyContain(c => c.Completada);
        }
        catch { huboError = true; throw; }
        finally
        {
            respuesta.TrySetResult(Result.Exito());
            try { await Task.WhenAll(tareas); }
            catch when (huboError) { /* Conserva el fallo de la comprobación original. */ }
        }
    }

    /// <summary>Las filas con una Gestión: QuickGrid rellena la página con filas vacías hasta su tamaño.</summary>
    private static List<IElement> FilasConGestion(IRenderedComponent<Gestiones> cut) =>
        cut.FindAll("tbody tr").Where(tr => tr.QuerySelector(".gestion-trabajador-centro") is not null).ToList();

    /// <summary>El hueco de acción rápida de la fila: «✓ Completar» si está pendiente, «Reabrir» si está completada.</summary>
    private static IElement BotonRapidoDeLaFila(IRenderedComponent<Gestiones> cut, string trabajador, string texto) =>
        FilaGestionFase1(cut, trabajador).QuerySelectorAll("button.gestion-completar, button.gestion-reabrir")
            .Single(b => b.TextContent.Replace("✓", string.Empty).Trim() == texto);

    /// <summary>
    /// Patrón de listados (2026-10-08): la fila no lleva menú «⋯». Cada acción del menú retirado
    /// conserva un sitio: completar y reabrir, el botón rápido de la fila y el pie de la vista
    /// rápida; «Vista rápida», el clic en la fila o en el nombre; «Eliminar», el pie de la vista rápida.
    /// </summary>
    [Fact]
    public async Task La_fila_no_lleva_menu_es_pulsable_y_cada_accion_del_menu_retirado_conserva_un_sitio()
    {
        var pendiente = Gestion("Nuria Salas Ortiz");
        var completada = Gestion("Bruno Vidal Camps", EstadoGestion.Completada);
        var cut = Renderizar(new MediatorFalso { Almacen = { pendiente, completada } });

        cut.WaitForAssertion(() => FilasConGestion(cut).Should().HaveCount(2, "la lista está pintada"));
        cut.FindAll("tbody .menu-acciones-disparador").Should().BeEmpty("la fila no lleva menú «⋯»");
        FilasConGestion(cut).Should().OnlyContain(f => f.ClassList.Contains("fila-pulsable"));
        FilasConGestion(cut).Should().OnlyContain(f => f.QuerySelectorAll(".nombre-abre-vista-rapida").Length == 1,
            "el clic de fila pulsa ese botón: tiene que haber uno, y solo uno, por fila");
        cut.FindAll("tbody a.boton-360-pagina").Should().BeEmpty("una Gestión no tiene página 360");
        BotonRapidoDeLaFila(cut, pendiente.TrabajadorNombre, "Completar").Should().NotBeNull();
        BotonRapidoDeLaFila(cut, completada.TrabajadorNombre, "Reabrir").Should().NotBeNull();

        await cut.Find("tbody tr .nombre-abre-vista-rapida").ClickAsync(new MouseEventArgs());

        cut.FindAll("aside.vista-rapida-gestion .pie-vista-rapida-gestion button").Select(b => b.TextContent.Trim())
            .Should().Contain("Eliminar", "sin menú de fila ni selección múltiple, eliminar vive en el pie de la vista rápida");
    }

    [Fact]
    public void Consulta_no_ve_los_botones_rapidos_de_la_fila()
    {
        this.ConRolDeEscritura(Roles.Consulta);
        var cut = Renderizar(new MediatorFalso { Almacen = { Gestion("Nuria Salas Ortiz"), Gestion("Bruno Vidal Camps", EstadoGestion.Completada) } });

        cut.WaitForAssertion(() => FilasConGestion(cut).Should().HaveCount(2, "la lista está pintada"));
        cut.FindAll("tbody button.gestion-completar, tbody button.gestion-reabrir").Should().BeEmpty();
    }

    /// <summary>Listados 5/7 (decisión D6, 2026-10-08): eliminar una Gestión deja «Deshacer», que la restaura.</summary>
    [Fact]
    public async Task Eliminar_ofrece_Deshacer_y_Deshacer_restaura_esa_gestion()
    {
        var abierta = Gestion("Nuria Salas Ortiz");
        var mediador = new MediatorFalso { Almacen = { Gestion("Juan Pérez Ibarra"), abierta } };
        var cut = Renderizar(mediador);

        await NombreEnLaFila(cut, "Nuria Salas Ortiz").ClickAsync(new MouseEventArgs());
        await BotonDeLaVistaRapida(cut, "Eliminar").ClickAsync(new MouseEventArgs());

        cut.Find("[role=dialog]").TextContent.Should().Contain("Podrás deshacerlo desde el aviso que aparecerá",
            "el diálogo ya no dice que no se puede recuperar: ahora hay «Deshacer» y restauración desde Auditoría");

        await BotonDelDialogo(cut, "Eliminar").ClickAsync(new MouseEventArgs());

        var avisos = Services.GetRequiredService<ToastService>();
        var aviso = avisos.Mensajes.Single(m => m.TextoAccion == "Deshacer");
        mediador.Enviadas.OfType<RestaurarGestionCommand>().Should().BeEmpty("ofrecer «Deshacer» no restaura nada");

        await cut.InvokeAsync(() => avisos.EjecutarAccionAsync(aviso.Id));

        mediador.Enviadas.OfType<RestaurarGestionCommand>().Should().Equal([new RestaurarGestionCommand(abierta.Id)]);
        avisos.Mensajes.Should().Contain(m => m.Mensaje == "Gestión restaurada." && m.Tono == TonoToast.Exito);
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

        // De «Pendientes» a «Completadas» son dos clics en la franja: desmarcar la una y marcar la otra (marcar
        // las dos sería «todas»). La consulta retenida es la primera, la de «Pendientes».
        cut.WaitForAssertion(() => retenida.Should().BeTrue());
        await AlternarEstadoGestionFase1(cut, "Pendientes");
        await AlternarEstadoGestionFase1(cut, "Completadas");
        UltimaGestionFase1(mediador).Estado.Should().Be(EstadoGestion.Completada);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ninguna gestión con estos filtros"));

        await cut.InvokeAsync(() => respuestaVieja.SetResult(
            mediador.Filtrar(new ObtenerGestionesQuery(null, EstadoGestion.Pendiente, null))));

        // Cualquier repintado posterior de la página enseña el estado que dejó
        // la respuesta vieja; se fuerza uno para no depender de cuál llegue.
        cut.Render();

        cut.Markup.Should().Contain("Ninguna gestión con estos filtros",
            "la respuesta vieja era de «Pendientes», no de la pregunta vigente");
        cut.Find(".cabecera-listado-contador").TextContent.Trim().Should().Be("0", "no hay coincidencias con el filtro vigente");
    }

    // ------------------------------------- La búsqueda viaja en la URL (T20)

    [Fact]
    public void Un_enlace_con_la_busqueda_la_lleva_a_la_consulta_y_a_la_caja()
    {
        var mediador = new MediatorFalso { Almacen = { Gestion("Juan Pérez Ibarra"), Gestion("Nuria Salas Ortiz") } };
        var cut = Renderizar(mediador, url: "gestiones?q=Salas");

        mediador.Enviadas.OfType<ObtenerGestionesQuery>().Last().Busqueda.Should().Be("Salas");
        cut.Find(".chip-filtro").TextContent.Should().Contain("Salas");
    }

    [Fact]
    public async Task Limpiar_todo_quita_la_busqueda_y_el_estado_de_la_url_en_una_sola_navegacion()
    {
        var mediador = new MediatorFalso { Almacen = { Gestion("Juan Pérez Ibarra"), Gestion("Nuria Salas Ortiz", EstadoGestion.Completada) } };
        var cut = Renderizar(mediador, url: "gestiones?q=Salas&estado=Completada");
        var navegacion = Services.GetRequiredService<NavigationManager>();
        // Un solo chip, el de la búsqueda: el estado se ve marcado en la franja, no en un chip.
        cut.WaitForAssertion(() => cut.FindAll(".chip-filtro").Should().HaveCount(1));
        cut.Find(".franja-estado-boton[data-estado='Completada']").GetAttribute("aria-pressed").Should().Be("true");
        var navegaciones = 0;
        navegacion.LocationChanged += (_, _) => navegaciones++;

        await cut.Find("button.limpiar-filtros-barra").ClickAsync(new MouseEventArgs());

        navegacion.Uri.Should().NotContain("q=").And.NotContain("estado=");
        navegaciones.Should().Be(1, "dos navegaciones seguidas se pisan: la segunda lee la URL sin el cambio de la primera");
        var consulta = mediador.Enviadas.OfType<ObtenerGestionesQuery>().Last();
        consulta.Busqueda.Should().BeNull();
        consulta.Estado.Should().BeNull();
        cut.WaitForAssertion(() => cut.FindAll(".chip-filtro").Should().BeEmpty());
    }

    /// <summary>
    /// El estado ya no tiene chip: se ve marcado en la franja («Todas», «Pendientes», «Completadas») y se quita
    /// desmarcándolo, también de la URL y de la consulta.
    /// </summary>
    [Fact]
    public async Task Desmarcar_el_estado_en_la_franja_lo_quita_tambien_de_la_url_y_de_la_consulta()
    {
        var mediador = new MediatorFalso { Almacen = { Gestion("Juan Pérez Ibarra"), Gestion("Iker Zubiaga Mena", EstadoGestion.Completada) } };
        var cut = Renderizar(mediador, estado: nameof(EstadoGestion.Completada));
        var navegacion = Services.GetRequiredService<NavigationManager>();

        cut.RotulosDeFranja().Should().Equal("Todas", "Pendientes", "Completadas");
        cut.MarcadosEnFranja().Should().Equal("Completadas");
        cut.FindAll(".chip-filtro").Should().BeEmpty("el estado se ve en la franja, no como chip");
        navegacion.Uri.Should().Contain("estado=Completada", "es el punto de partida de este caso");
        UltimaGestionFase1(mediador).Estado.Should().Be(EstadoGestion.Completada, "es el punto de partida de este caso");

        await AlternarEstadoGestionFase1(cut, "Completadas");

        navegacion.Uri.Should().NotContain("estado=");
        UltimaGestionFase1(mediador).Estado.Should().BeNull();
        cut.WaitForAssertion(() => cut.MarcadosEnFranja().Should().Equal("Todas"));
        cut.WaitForAssertion(() => cut.Find(".cabecera-listado-contador").TextContent.Trim().Should().Be("2"));
    }

    /// <summary>
    /// La consulta de Gestiones filtra por un solo estado. Con los dos botones marcados no hay nada que filtrar
    /// (son todos los estados que existen): la consulta va sin estado, pero la URL y la franja conservan la
    /// selección tal cual; y «Todas» la borra.
    /// </summary>
    [Fact]
    public async Task Con_los_dos_estados_marcados_la_consulta_va_sin_estado_y_la_url_conserva_la_seleccion()
    {
        var mediador = new MediatorFalso { Almacen = { Gestion("Juan Pérez Ibarra"), Gestion("Iker Zubiaga Mena", EstadoGestion.Completada) } };
        var cut = Renderizar(mediador);
        var navegacion = Services.GetRequiredService<NavigationManager>();

        await AlternarEstadoGestionFase1(cut, "Pendientes");

        UltimaGestionFase1(mediador).Estado.Should().Be(EstadoGestion.Pendiente);
        navegacion.Uri.Should().EndWith("estado=Pendiente");
        cut.WaitForAssertion(() => TrabajadoresDeLasFilas(cut).Should().Equal("Juan Pérez Ibarra"));

        await AlternarEstadoGestionFase1(cut, "Completadas");

        UltimaGestionFase1(mediador).Estado.Should().BeNull("dos estados marcados son todos");
        Uri.UnescapeDataString(navegacion.Uri).Should().EndWith("estado=Pendiente,Completada");
        cut.WaitForAssertion(() => cut.MarcadosEnFranja().Should().Equal("Pendientes", "Completadas"));
        cut.WaitForAssertion(() => TrabajadoresDeLasFilas(cut).Should().HaveCount(2));

        await AlternarEstadoGestionFase1(cut, "Todas");

        UltimaGestionFase1(mediador).Estado.Should().BeNull();
        navegacion.Uri.Should().NotContain("estado=");
        cut.WaitForAssertion(() => cut.MarcadosEnFranja().Should().Equal("Todas"));
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
        cut.Find(".cabecera-listado-contador").TextContent.Trim().Should().Be("1");
    }

    [Fact]
    public void Sin_filtros_el_conteo_no_habla_de_ningun_filtro()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Gestion("Juan Pérez Ibarra"), Gestion("Nuria Salas Ortiz") } });

        cut.Find(".cabecera-listado-contador").TextContent.Trim().Should().Be("2");
        cut.FindAll(".chip-filtro").Should().BeEmpty();
    }

    /// <summary>
    /// Las dos combinaciones del conteo que los casos de arriba no pintan
    /// (una sin filtros, varias con filtros) y el chip de la búsqueda: cada
    /// una es una clave propia de <c>TextosGestiones</c>.
    /// </summary>
    [Fact]
    public async Task El_conteo_de_una_sin_filtros_y_de_varias_con_filtros_y_el_chip_de_busqueda()
    {
        var mediador = new MediatorFalso { Almacen = { Gestion("Juan Pérez Ibarra") } };
        var cut = Renderizar(mediador);

        cut.Find(".cabecera-listado-contador").TextContent.Trim().Should().Be("1");

        mediador.Almacen.Add(Gestion("Nuria Salas Ortiz"));
        await cut.InvokeAsync(() => cut.FindComponent<CampoTexto>().Instance.ValorChanged.InvokeAsync("a"));

        cut.WaitForAssertion(() => cut.Find(".cabecera-listado-contador").TextContent.Trim().Should().Be("2"));
        cut.Find(".chip-filtro").TextContent.Trim().Should().Be("Búsqueda: \"a\"");
    }

    private static IElement CabeceraOrdenable(IRenderedComponent<Gestiones> cut, string titulo) =>
        cut.FindAll("thead th").Single(th => th.TextContent.Trim().StartsWith(titulo, StringComparison.Ordinal)).QuerySelector("button.col-title")!;

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

        await ElegirOrdenGestionFase1(cut, nameof(GestionListaDto.CentroNombre));

        var ascendente = mediador.Enviadas.OfType<ObtenerGestionesQuery>().Last();
        ascendente.OrdenarPor.Should().Be(nameof(GestionListaDto.CentroNombre));
        ascendente.Descendente.Should().BeFalse();
        cut.WaitForAssertion(() => ColumnaDeLasFilas(cut, 1).Should().Equal(["Centro Este", "Centro Norte", "Centro Sur"]));

        await CabeceraOrdenable(cut, "Trabajador").ClickAsync(new MouseEventArgs());

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
        enlace.GetAttribute("href").Should().Be("bandeja", "con un solo Tenant autorizado, Mi trabajo es /bandeja en el menú");
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

    [Fact]
    public async Task El_error_de_recarga_conserva_la_suscripcion_de_teclado_y_no_abre_datos_anteriores()
    {
        var modulo = JSInterop.SetupModule("./js/atajos-lista.js");
        modulo.Mode = JSRuntimeMode.Loose;
        var mediador = new MediatorFalso { Almacen = { Gestion("Juan Pérez Ibarra") } };
        var cut = Renderizar(mediador);
        cut.WaitForAssertion(() => modulo.Invocations["registrarAtajosLista"].Should().ContainSingle());
        var referencia = modulo.Invocations["registrarAtajosLista"].Single().Arguments[0]
            .Should().BeOfType<DotNetObjectReference<AtajosListaTeclado>>().Which;
        await cut.InvokeAsync(() => referencia.Value.RecibirAtajo("j"));

        mediador.FalloConsulta = new InvalidOperationException("caída de la base");
        await cut.InvokeAsync(() => CajaDeBusqueda(cut).Instance.ValorChanged.InvokeAsync("Juan"));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No pudimos cargar las gestiones"));
        await cut.InvokeAsync(() => referencia.Value.RecibirAtajo("Enter"));
        cut.FindAll("aside.vista-rapida-gestion").Should().BeEmpty();
        mediador.Enviadas.OfType<CompletarGestionCommand>().Should().BeEmpty();

        mediador.FalloConsulta = null;
        await cut.FindAll(".estado-vacio button").Single(b => b.TextContent.Trim() == "Reintentar")
            .ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => TrabajadoresDeLasFilas(cut).Should().Equal("Juan Pérez Ibarra"));
        modulo.Invocations["registrarAtajosLista"].Should().ContainSingle("la suscripción original permanece disponible");
        await cut.InvokeAsync(() => referencia.Value.RecibirAtajo("j"));
        await cut.InvokeAsync(() => referencia.Value.RecibirAtajo("Enter"));
        cut.Find("aside.vista-rapida-gestion .nombre-vista-rapida-gestion").TextContent.Trim()
            .Should().Be("Juan Pérez Ibarra");
    }

    // --- Recuento de consultas ----------------------------------------------------------------

    private static int ConsultasDeLista(MediatorFalso mediador) =>
        mediador.Enviadas.OfType<ObtenerGestionesQuery>().Count();

    /// <summary>
    /// Solo la primera columna: en Gen 2 el Centro también es un enlace con la
    /// misma clase, así que <c>td .enlace-nombre-fila</c> a secas devuelve dos
    /// textos por fila y no distingue filtrar de no filtrar.
    /// </summary>
    private static IEnumerable<string> TrabajadoresDeLasFilas(IRenderedComponent<Gestiones> cut) =>
        cut.FindAll("tbody .gestion-trabajador-centro .enlace-nombre-fila:not(.gestion-centro)").Select(e => e.TextContent.Trim());

    private static IRenderedComponent<CampoTexto> CajaDeBusqueda(IRenderedComponent<Gestiones> cut) =>
        cut.FindComponents<CampoTexto>().First(c => c.Instance.Placeholder?.StartsWith("Filtrar esta pantalla") == true);

    /// <summary>
    /// Buscar recarga la lista UNA vez.
    /// <c>PaginationState.SetCurrentPageIndexAsync</c> ya avisa a QuickGrid
    /// aunque la página no cambie, así que llamar además a <c>RefreshDataAsync</c>
    /// pedía dos veces lo mismo por cada búsqueda.
    ///
    /// <para>
    /// La primera búsqueda solo sirve para asentar el total en 1: se mide la
    /// SEGUNDA, de «Ana» a «Luis», con el total quieto. Si el total cambiara,
    /// QuickGrid volvería a pedir la misma página por su cuenta y el recuento
    /// mezclaría esa repetición con lo que pide la página.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Buscar_sin_cambiar_el_total_hace_una_sola_consulta()
    {
        var mediador = new MediatorFalso { Almacen = { Gestion("Ana Vega Ortiz"), Gestion("Luis Salas Moreno") } };
        var cut = Renderizar(mediador);

        await cut.InvokeAsync(() => CajaDeBusqueda(cut).Instance.ValorChanged.InvokeAsync("Ana"));
        cut.WaitForAssertion(() => TrabajadoresDeLasFilas(cut).Should().Equal("Ana Vega Ortiz"));
        var consultasAntes = ConsultasDeLista(mediador);

        await cut.InvokeAsync(() => CajaDeBusqueda(cut).Instance.ValorChanged.InvokeAsync("Luis"));

        cut.WaitForAssertion(() => TrabajadoresDeLasFilas(cut).Should().Equal("Luis Salas Moreno"));
        (ConsultasDeLista(mediador) - consultasAntes).Should().Be(1,
            "avisar a la paginación y refrescar la rejilla son dos formas de pedir lo mismo: juntas costaban dos consultas por búsqueda");
    }

    /// <summary>
    /// Cambiar el tamaño de página pide la página 1 del tamaño nuevo UNA vez.
    /// Mismo motivo que <see cref="Buscar_sin_cambiar_el_total_hace_una_sola_consulta"/>;
    /// aquí el total no cambia solo (tres gestiones antes y después), así que
    /// lo que se cuente es de la página.
    /// </summary>
    [Fact]
    public async Task Cambiar_el_tamano_de_pagina_hace_una_sola_consulta()
    {
        var mediador = new MediatorFalso
        {
            Almacen = { Gestion("Ana Vega Ortiz"), Gestion("Luis Salas Moreno"), Gestion("Bea Alonso Ruiz") }
        };
        var cut = Renderizar(mediador);
        var consultasAntes = ConsultasDeLista(mediador);

        await cut.Find(".paginador-tamano-select").ChangeAsync(new ChangeEventArgs { Value = "50" });

        cut.WaitForAssertion(() => mediador.Enviadas.OfType<ObtenerGestionesQuery>().Last().TamanoPagina.Should().Be(50));
        mediador.Enviadas.OfType<ObtenerGestionesQuery>().Last().Pagina.Should().Be(1);
        (ConsultasDeLista(mediador) - consultasAntes).Should().Be(1,
            "el total sigue siendo 3: la única consulta que cabe contar es la del tamaño nuevo");
    }

    // Integrar dentro de GestionesListaGen2Tests; fixture existente, sin reflexión ni orden sintético del proveedor.
    public static IEnumerable<object[]> OrdenCombinadoGestionFase1()
    {
        foreach (var campo in new[] { "TrabajadorNombre", "CentroNombre" })
            foreach (var misma in new[] { false, true })
                foreach (var descendente in new[] { false, true })
                    yield return [campo, misma, descendente];
    }

    [Theory]
    [MemberData(nameof(OrdenCombinadoGestionFase1))]
    public async Task Opciones_nativas_conservan_campo_y_sentido_de_gestiones(string campo, bool misma, bool descendente)
    {
        var m = new MediatorFalso { Almacen = { Gestion("Bea", centro: "Centro A"), Gestion("Ana", centro: "Centro Z") } };
        var cut = Renderizar(m);
        var previo = campo == "TrabajadorNombre" ? "CentroNombre" : "TrabajadorNombre";
        await ElegirOrdenGestionFase1(cut, previo);
        await FijarSentidoGestionFase1(cut, m, misma ? "Trabajador" : "Estado", descendente);
        UltimaGestionFase1(m).OrdenarPor.Should().Be(misma ? previo : "Estado");
        UltimaGestionFase1(m).Descendente.Should().Be(descendente);
        var antes = ConsultasDeLista(m);
        await ElegirOrdenGestionFase1(cut, campo);
        cut.WaitForAssertion(() =>
        {
            ConsultasDeLista(m).Should().Be(antes + 1);
            UltimaGestionFase1(m).OrdenarPor.Should().Be(campo);
            UltimaGestionFase1(m).Descendente.Should().Be(misma && descendente);
        });
    }

    [Theory]
    [InlineData("Tipo de documento", "TipoDocumentoNombre", false)]
    [InlineData("Tipo de documento", "TipoDocumentoNombre", true)]
    [InlineData("Estado", "Estado", false)]
    [InlineData("Estado", "Estado", true)]
    [InlineData("Creada", "CreadoEnUtc", false)]
    [InlineData("Creada", "CreadoEnUtc", true)]
    public async Task Los_otros_tres_campos_envian_orden_real(string titulo, string campo, bool descendente)
    {
        var m = new MediatorFalso { Almacen = { Gestion("Ana"), Gestion("Bea", EstadoGestion.Completada) } };
        var cut = Renderizar(m);
        await FijarSentidoGestionFase1(cut, m, titulo, descendente);
        UltimaGestionFase1(m).OrdenarPor.Should().Be(campo);
        UltimaGestionFase1(m).Descendente.Should().Be(descendente);
    }

    [Fact]
    public void Orden_inicial_pendientes_primero_conserva_datos_y_total_medido()
    {
        var antigua = Gestion("Ana antigua", centro: "Centro Norte", documento: "Formación") with { CreadoEnUtc = new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc) };
        var nueva = Gestion("Bea nueva", centro: "Centro Sur", documento: "Reconocimiento") with { CreadoEnUtc = nuevaFechaGestionFase1 };
        var completa = Gestion("Carla completada", EstadoGestion.Completada) with { CreadoEnUtc = nuevaFechaGestionFase1.AddDays(1) };
        var m = new MediatorFalso { Almacen = { completa, antigua, nueva } };
        var cut = Renderizar(m);
        UltimaGestionFase1(m).OrdenarPor.Should().Be("Estado");
        UltimaGestionFase1(m).Descendente.Should().BeFalse();
        TrabajadoresDeLasFilas(cut).Should().Equal("Bea nueva", "Ana antigua", "Carla completada");
        var fila = FilaGestionFase1(cut, nueva.TrabajadorNombre);
        var celda = fila.QuerySelector(".gestion-trabajador-centro")!;
        celda.TextContent.Should().Contain(nueva.TrabajadorNombre).And.Contain(nueva.CentroNombre);
        fila.TextContent.Should().Contain(nueva.TipoDocumentoNombre).And.Contain(nueva.CreadoEnUtc.ToString("dd/MM/yyyy"));
        cut.Find(".cabecera-listado-contador").TextContent.Trim().Should().Be("3");
    }

    private static readonly DateTime nuevaFechaGestionFase1 = new(2026, 8, 14, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Completar_de_fila_envia_ID_y_true_y_desaparece_del_filtro_pendientes()
    {
        var a = Gestion("Ana pendiente");
        var b = Gestion("Bea pendiente");
        var m = new MediatorFalso { Almacen = { a, b } };
        var cut = Renderizar(m, nameof(EstadoGestion.Pendiente));
        UltimaGestionFase1(m).Estado.Should().Be(EstadoGestion.Pendiente);
        await FilaGestionFase1(cut, a.TrabajadorNombre).QuerySelector("button.gestion-completar")!.ClickAsync(new MouseEventArgs());
        m.Enviadas.OfType<CompletarGestionCommand>().Should().Equal(new CompletarGestionCommand(a.Id, true));
        m.Almacen.Single(g => g.Id == a.Id).Estado.Should().Be(EstadoGestion.Completada);
        cut.WaitForAssertion(() => TrabajadoresDeLasFilas(cut).Should().Equal(b.TrabajadorNombre));
        UltimaGestionFase1(m).Estado.Should().Be(EstadoGestion.Pendiente);
        cut.Find(".cabecera-listado-contador").TextContent.Trim().Should().Be("1");
    }

    [Theory]
    [InlineData(EstadoGestion.Completada, false)]
    [InlineData(EstadoGestion.Pendiente, true)]
    public async Task Completar_no_se_ofrece_a_completada_ni_a_Consulta_con_lista_funcional(EstadoGestion estado, bool consulta)
    {
        if (consulta) this.ConRolDeEscritura(Roles.Consulta);
        var a = Gestion("Ana visible", estado);
        var m = new MediatorFalso { Almacen = { a } };
        var cut = Renderizar(m, estado.ToString());
        UltimaGestionFase1(m).Estado.Should().Be(estado);
        TrabajadoresDeLasFilas(cut).Should().Equal(a.TrabajadorNombre);
        cut.FindAll("tbody button.gestion-completar").Should().BeEmpty();
        await NombreEnLaFila(cut, a.TrabajadorNombre).ClickAsync(new MouseEventArgs());
        cut.Find("aside.vista-rapida-gestion .nombre-vista-rapida-gestion").TextContent.Trim().Should().Be(a.TrabajadorNombre);
        m.Enviadas.OfType<CompletarGestionCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task j_k_Enter_usan_la_fila_enfocada_y_x_no_activa_panel_ni_comandos()
    {
        var a = Gestion("Ana foco", centro: "Centro A");
        var b = Gestion("Bea foco", centro: "Centro B");
        var m = new MediatorFalso { Almacen = { a, b } };
        var cut = Renderizar(m);
        await AtajoGestionFase1(cut, "j");
        FilaGestionFase1(cut, a.TrabajadorNombre).ClassList.Should().Contain("fila-enfocada");
        var solicitudes = m.Enviadas.Count;
        await AtajoGestionFase1(cut, "x");
        FilaGestionFase1(cut, a.TrabajadorNombre).ClassList.Should().Contain("fila-enfocada");
        cut.FindAll("aside.vista-rapida-gestion").Should().BeEmpty();
        m.Enviadas.Should().HaveCount(solicitudes);
        await AtajoGestionFase1(cut, "j");
        FilaGestionFase1(cut, b.TrabajadorNombre).ClassList.Should().Contain("fila-enfocada");
        FilaGestionFase1(cut, a.TrabajadorNombre).ClassList.Should().NotContain("fila-enfocada");
        await AtajoGestionFase1(cut, "k");
        FilaGestionFase1(cut, a.TrabajadorNombre).ClassList.Should().Contain("fila-enfocada");
        await AtajoGestionFase1(cut, "j");
        await AtajoGestionFase1(cut, "Enter");
        cut.Find("aside.vista-rapida-gestion .nombre-vista-rapida-gestion").TextContent.Trim().Should().Be(b.TrabajadorNombre);
        cut.Find("aside.vista-rapida-gestion").TextContent.Should().Contain(b.CentroNombre);
        m.Enviadas.OfType<CompletarGestionCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task Buscar_invalida_snapshot_y_Enter_no_abre_la_fila_anterior_mientras_carga()
    {
        var m = new MediatorFalso { Almacen = { Gestion("Ana antigua"), Gestion("Bea nueva") } };
        var cut = Renderizar(m);
        await AtajoGestionFase1(cut, "j");
        var respuesta = m.Filtrar(new ObtenerGestionesQuery("nueva", null, null));
        var retenida = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.Retener = q => q is ObtenerGestionesQuery { Busqueda: "nueva" } ? retenida.Task : null;
        Task? buscar = null;
        var huboError = false;
        try
        {
            buscar = cut.InvokeAsync(() => CajaDeBusqueda(cut).Instance.ValorChanged.InvokeAsync("nueva"));
            cut.WaitForAssertion(() => UltimaGestionFase1(m).Busqueda.Should().Be("nueva"));
            await AtajoGestionFase1(cut, "j");
            await AtajoGestionFase1(cut, "Enter");
            cut.FindAll("aside.vista-rapida-gestion").Should().BeEmpty();
        }
        catch { huboError = true; throw; }
        finally
        {
            retenida.TrySetResult(respuesta);
            if (buscar is not null)
            {
                try { await buscar; }
                catch when (huboError) { /* Conserva la aserción original. */ }
            }
        }
        await AtajoGestionFase1(cut, "j");
        await AtajoGestionFase1(cut, "Enter");
        cut.Find("aside.vista-rapida-gestion .nombre-vista-rapida-gestion").TextContent.Trim().Should().Be("Bea nueva");
    }

    [Fact]
    public async Task Respuesta_superada_no_publica_snapshot_para_Enter()
    {
        var m = new MediatorFalso { Almacen = { Gestion("Ana antigua"), Gestion("Bea nueva") } };
        var cut = Renderizar(m);
        var respuesta = m.Filtrar(new ObtenerGestionesQuery("antigua", null, null));
        var retenida = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.Retener = q => q is ObtenerGestionesQuery { Busqueda: "antigua" } ? retenida.Task : null;
        Task? anterior = null;
        var huboError = false;
        try
        {
            anterior = cut.InvokeAsync(() => CajaDeBusqueda(cut).Instance.ValorChanged.InvokeAsync("antigua"));
            cut.WaitForAssertion(() => UltimaGestionFase1(m).Busqueda.Should().Be("antigua"));
            await cut.InvokeAsync(() => CajaDeBusqueda(cut).Instance.ValorChanged.InvokeAsync("nueva"));
            UltimaGestionFase1(m).Busqueda.Should().Be("nueva");
            TrabajadoresDeLasFilas(cut).Should().Equal("Bea nueva");
            await AtajoGestionFase1(cut, "j");
            await AtajoGestionFase1(cut, "Enter");
            cut.Find("aside.vista-rapida-gestion .nombre-vista-rapida-gestion").TextContent.Trim().Should().Be("Bea nueva");
        }
        catch { huboError = true; throw; }
        finally
        {
            retenida.TrySetResult(respuesta);
            if (anterior is not null)
            {
                try { await anterior; }
                catch when (huboError) { /* Conserva la aserción original. */ }
            }
        }
        await AtajoGestionFase1(cut, "k");
        await AtajoGestionFase1(cut, "Enter");
        cut.Find("aside.vista-rapida-gestion .nombre-vista-rapida-gestion").TextContent.Trim().Should().Be("Bea nueva");
        TrabajadoresDeLasFilas(cut).Should().Equal("Bea nueva");
    }

    private static ObtenerGestionesQuery UltimaGestionFase1(MediatorFalso m) => m.Enviadas.OfType<ObtenerGestionesQuery>().Last();
    private static IElement FilaGestionFase1(IRenderedComponent<Gestiones> cut, string nombre) =>
        cut.FindAll("tbody tr").Single(tr => tr.QuerySelector(".gestion-trabajador-centro .enlace-nombre-fila")?.TextContent.Trim() == nombre);
    private static IElement CabeceraGestionFase1(IRenderedComponent<Gestiones> cut, string titulo) =>
        cut.FindAll("thead th").Single(th => th.TextContent.Trim().StartsWith(titulo, StringComparison.Ordinal));
    private static async Task FijarSentidoGestionFase1(IRenderedComponent<Gestiones> cut, MediatorFalso m, string titulo, bool descendente)
    {
        await CabeceraGestionFase1(cut, titulo).QuerySelector("button.col-title")!.ClickAsync(new MouseEventArgs());
        if (UltimaGestionFase1(m).Descendente != descendente)
            await CabeceraGestionFase1(cut, titulo).QuerySelector("button.col-title")!.ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => UltimaGestionFase1(m).Descendente.Should().Be(descendente));
    }
    private static async Task ElegirOrdenGestionFase1(IRenderedComponent<Gestiones> cut, string campo)
    {
        await CabeceraGestionFase1(cut, "Trabajador").QuerySelector("button.col-options-button")!.ClickAsync(new MouseEventArgs());
        var texto = campo == "CentroNombre" ? "Ordenar por centro" : "Ordenar por trabajador";
        await cut.FindAll(".gestiones-opciones-orden button").Single(b => b.TextContent.Trim() == texto).ClickAsync(new MouseEventArgs());
    }
    private static Task AtajoGestionFase1(IRenderedComponent<Gestiones> cut, string tecla) =>
        cut.InvokeAsync(() => cut.FindComponent<AtajosListaTeclado>().Instance.OnAtajo.InvokeAsync(tecla));
    /// <summary>Marca o desmarca un botón de la franja de estado por su rótulo («Todas», «Pendientes», «Completadas»).</summary>
    private static Task AlternarEstadoGestionFase1(IRenderedComponent<Gestiones> cut, string rotulo) =>
        cut.BotonDeFranja(rotulo).ClickAsync(new MouseEventArgs());


    // ---- Exportar (decisión D2 del 2026-10-08): dos entradas en el «⋯» de la cabecera ----

    [Fact]
    public void El_menu_de_cabecera_ofrece_exportar_esta_vista_con_sus_criterios_y_exportar_todo()
    {
        var m = new MediatorFalso { Almacen = { Gestion("Juan Pérez Ibarra") } };
        var cut = Renderizar(m, url: "gestiones?q=Juan&estado=Pendiente");

        cut.Find("header.cabecera-pagina .menu-acciones-disparador").Click();

        cut.FindAll("header.cabecera-pagina .menu-acciones-item").Select(i => i.TextContent.Trim())
            .Should().Equal("Exportar esta vista (filas: 1)", "Exportar todo");
        var enlaces = cut.FindAll("header.cabecera-pagina a.menu-acciones-item").Select(i => i.GetAttribute("href")).ToList();
        enlaces[0].Should().StartWith("/gestiones/exportar.xlsx?q=Juan&estado=Pendiente");
        enlaces[1].Should().Be("/gestiones/exportar.xlsx");
    }
}
