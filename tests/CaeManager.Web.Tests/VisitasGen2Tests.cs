using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Application.Visitas.Commands.EliminarVisita;
using CaeManager.Application.Visitas.Commands.MarcarNotificadoCliente;
using CaeManager.Application.Visitas.Queries.ObtenerDetalleVisita;
using CaeManager.Application.Visitas.Queries.ObtenerDocumentacionVisita;
using CaeManager.Application.Visitas.Queries.ObtenerVisitaPorId;
using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Visitas;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Visitas.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Visitas en Gen 2 (Visitas TALVEG.dc.html): carreras asíncronas de la lista,
/// del detalle y del formulario; el interruptor de «notificada»; la
/// comprobación previa; la eliminación con diálogo; y el vocabulario visible.
///
/// <para>
/// El doble del mediador <b>aplica</b> los filtros de la consulta y los
/// comandos a su propio estado: una pantalla que no enviara un filtro, o que
/// no recargara tras un comando, se vería distinta — no basta con que el test
/// recorra el camino.
/// </para>
/// </summary>
public class VisitasGen2Tests : BunitContext
{
    public VisitasGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly DateOnly Hoy = DateOnly.FromDateTime(DateTime.UtcNow);

    private sealed class MediatorVisitas : IMediator
    {
        public List<VisitaListaDto> Visitas { get; } = [];

        /// <summary>Si es cierto, cada carga de la lista queda pendiente hasta que el test la resuelva.</summary>
        public bool DiferirLista { get; set; }

        public List<(ObtenerVisitasQuery Consulta, TaskCompletionSource<ResultadoPaginado<VisitaListaDto>> Respuesta)> CargasPendientes { get; } = [];

        public Dictionary<Guid, TaskCompletionSource<DetalleVisitaDto?>> DetallesDiferidos { get; } = [];

        public Dictionary<Guid, TaskCompletionSource<VisitaDetalleDto?>> EdicionesDiferidas { get; } = [];

        public DocumentacionVisitaDto? Documentacion { get; set; }

        public bool DiferirDocumentacion { get; set; }

        public List<TaskCompletionSource<DocumentacionVisitaDto>> CargasDocumentacionPendientes { get; } = [];

        public TramoAntelacion? Tramo { get; set; }

        public List<object> Comandos { get; } = [];

        public int ConsultasVisitas { get; private set; }

        public TaskCompletionSource<Result>? NotificacionDiferida { get; set; }

        public Exception? ErrorNotificacion { get; set; }

        public static DetalleVisitaDto Detalle(VisitaListaDto v, TramoAntelacion? tramo = null) => new(
            v.Id, v.CentroNombre, v.ClienteRazonSocial, v.EmpresaId, v.EmpresaRazonSocial, v.FechaInicio, v.FechaFin,
            Notas: null, v.NotificadoCliente, Trabajadores: [], HoraEstimadaAcceso: null, FechaHoraSolicitudUtc: null,
            FechaHoraExpedienteCompletoUtc: null, AntelacionNominalHoras: 36m, AntelacionEfectivaHoras: 11m, tramo,
            AtribucionUrgencia.SinUrgencia);

        public static VisitaDetalleDto ParaEditar(VisitaListaDto v) => new(
            v.Id, v.CentroId, v.CentroNombre, v.ClienteRazonSocial, v.EmpresaRazonSocial, v.FechaInicio, v.FechaFin,
            TrabajadorIds: [], v.NotificadoCliente, Notas: null, HoraEstimadaAcceso: null, Version: Guid.NewGuid());

        private List<VisitaListaDto> Aplicar(ObtenerVisitasQuery consulta) => Visitas
            .Where(v => consulta.NotificadoCliente is null || v.NotificadoCliente == consulta.NotificadoCliente)
            .Where(v => !consulta.SoloUrgentes || v.NivelUrgencia != NivelUrgenciaVisita.Normal)
            .Where(v => !consulta.SoloActivas || v.FechaFin >= Hoy)
            .Where(v => string.IsNullOrWhiteSpace(consulta.Busqueda)
                || $"{v.CentroNombre} {v.ClienteRazonSocial} {v.EmpresaRazonSocial}".Contains(consulta.Busqueda, StringComparison.OrdinalIgnoreCase))
            .ToList();

        private static Task<T> Respuesta<T>(object valor) => Task.FromResult((T)valor);

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            switch (request)
            {
                case ObtenerVisitasQuery consulta:
                    ConsultasVisitas++;
                    if (DiferirLista)
                    {
                        var pendiente = new TaskCompletionSource<ResultadoPaginado<VisitaListaDto>>();
                        CargasPendientes.Add((consulta, pendiente));
                        return (Task<TResponse>)(object)pendiente.Task;
                    }

                    var filas = Aplicar(consulta);
                    return Respuesta<TResponse>(new ResultadoPaginado<VisitaListaDto>(filas, filas.Count, consulta.Pagina, consulta.TamanoPagina));

                case ObtenerDetalleVisitaQuery detalle:
                    return DetallesDiferidos.TryGetValue(detalle.Id, out var detalleDiferido)
                        ? (Task<TResponse>)(object)detalleDiferido.Task
                        : Respuesta<TResponse>(Detalle(Visitas.Single(v => v.Id == detalle.Id), Tramo));

                case ObtenerVisitaPorIdQuery edicion:
                    return EdicionesDiferidas.TryGetValue(edicion.Id, out var edicionDiferida)
                        ? (Task<TResponse>)(object)edicionDiferida.Task
                        : Respuesta<TResponse>(ParaEditar(Visitas.Single(v => v.Id == edicion.Id)));

                case ObtenerDocumentacionVisitaQuery:
                    if (DiferirDocumentacion)
                    {
                        var pendiente = new TaskCompletionSource<DocumentacionVisitaDto>();
                        CargasDocumentacionPendientes.Add(pendiente);
                        return (Task<TResponse>)(object)pendiente.Task;
                    }

                    return Respuesta<TResponse>(Documentacion
                        ?? new DocumentacionVisitaDto(Guid.NewGuid(), new SeccionDocumentacionDto(EstadoDocumento.Vigente, []), []));

                case MarcarNotificadoClienteCommand marcar:
                    Comandos.Add(marcar);
                    if (ErrorNotificacion is { } error)
                        return Task.FromException<TResponse>(error);

                    if (NotificacionDiferida is { } notificacionDiferida)
                        return (Task<TResponse>)(object)notificacionDiferida.Task;

                    var indice = Visitas.FindIndex(v => v.Id == marcar.Id);
                    Visitas[indice] = Visitas[indice] with { NotificadoCliente = marcar.Notificado };
                    return Respuesta<TResponse>(Result.Exito());

                case EliminarVisitaCommand eliminar:
                    Comandos.Add(eliminar);
                    Visitas.RemoveAll(v => v.Id == eliminar.Id);
                    return Respuesta<TResponse>(Result.Exito());

                case ObtenerCentrosParaSelectorQuery:
                    return Respuesta<TResponse>(Array.Empty<CentroSelectorDto>());

                case ObtenerTrabajadoresParaSelectorQuery:
                    return Respuesta<TResponse>(Array.Empty<TrabajadorSelectorDto>());

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

    private static VisitaListaDto Visita(
        string centro, bool notificado = false, NivelUrgenciaVisita urgencia = NivelUrgenciaVisita.Urgente) =>
        new(Guid.NewGuid(), Guid.NewGuid(), centro, Guid.NewGuid(), "Iberojet S.A.", Guid.NewGuid(), "Instalaciones Arbeko S.L.",
            Hoy, Hoy.AddDays(2), TotalTrabajadores: 3, DocumentacionCompleta: false, notificado, OrigenVisita.Correo, urgencia);

    private static ResultadoPaginado<VisitaListaDto> Pagina(params VisitaListaDto[] visitas) =>
        new(visitas, visitas.Length, 1, 20);

    private IRenderedComponent<Visitas> Renderizar(MediatorVisitas mediator)
    {
        Services.AddScoped<IMediator>(_ => mediator);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        return Render<Visitas>();
    }

    private static IElement Fila(IRenderedComponent<Visitas> cut, string centro) =>
        cut.FindAll("tbody tr").First(tr => tr.TextContent.Contains(centro));

    private static IElement Interruptor(IRenderedComponent<Visitas> cut, string centro) =>
        Fila(cut, centro).QuerySelector("input.visitas-interruptor")
            ?? throw new InvalidOperationException($"La fila de {centro} no tiene interruptor.");

    private static IElement FiltroCheckbox(IRenderedComponent<Visitas> cut, string texto) =>
        cut.FindAll("label.filtro-critico").First(l => l.TextContent.Contains(texto)).QuerySelector("input")!;

    /// <summary>Abre el menú de la fila y devuelve el ítem pedido, sin pulsarlo.</summary>
    private static IElement ItemDeMenu(IRenderedComponent<Visitas> cut, string centro, string accion)
    {
        Fila(cut, centro).QuerySelector(".menu-acciones-disparador")!.Click();
        return Fila(cut, centro).QuerySelectorAll(".menu-acciones-item").First(i => i.TextContent.Trim() == accion);
    }

    /// <summary>
    /// Dos cambios de filtro seguidos lanzan dos cargas. Si la del filtro
    /// ANTERIOR vuelve la última, no puede pisar el total del filtro actual:
    /// antes lo hacía, y el estado vacío «con estos filtros» desaparecía
    /// dejando una lista vacía sin explicación.
    /// </summary>
    [Fact]
    public async Task Una_carga_de_un_filtro_anterior_que_llega_tarde_no_pisa_la_del_filtro_actual()
    {
        var mediator = new MediatorVisitas { DiferirLista = true };
        var cut = Renderizar(mediator);

        await cut.InvokeAsync(() => mediator.CargasPendientes.Single().Respuesta.SetResult(Pagina(Visita("Centro Norte"))));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Centro Norte"));

        // Medido, no supuesto: cada cambio de filtro pide DOS cargas seguidas
        // (RecargarAsync: SetCurrentPageIndexAsync(0) refresca el grid y luego
        // RefreshDataAsync vuelve a hacerlo), y la segunda solo sale cuando la
        // primera responde. La carrera que no se cura sola es la de la
        // SEGUNDA carga del filtro anterior llegando después de todo lo del
        // filtro nuevo: nada vuelve a cargar detrás de ella.
        //
        // Las tareas de los manejadores se guardan sin esperar y se esperan al
        // final como barrera: la primera versión de este test comprobaba antes
        // de que la respuesta tardía se aplicara, y dio verde con la guarda
        // quitada (mutación M1).
        var cambioUrgentes = FiltroCheckbox(cut, "Solo urgentes").ChangeAsync(new ChangeEventArgs { Value = true });
        cut.WaitForAssertion(() => mediator.CargasPendientes.Should().HaveCount(2));
        await cut.InvokeAsync(() => mediator.CargasPendientes[1].Respuesta.SetResult(Pagina(Visita("Centro Norte"))));
        cut.WaitForAssertion(() => mediator.CargasPendientes.Should().HaveCount(3));
        var anterior = mediator.CargasPendientes[2];

        var cambioNotificado = cut.Find(".barra-filtros select").ChangeAsync(new ChangeEventArgs { Value = "no" });
        cut.WaitForAssertion(() => mediator.CargasPendientes.Should().HaveCount(4));
        await cut.InvokeAsync(() => mediator.CargasPendientes[3].Respuesta.SetResult(Pagina()));
        cut.WaitForAssertion(() => mediator.CargasPendientes.Should().HaveCount(5));
        var actual = mediator.CargasPendientes[4];

        anterior.Consulta.SoloUrgentes.Should().BeTrue();
        anterior.Consulta.NotificadoCliente.Should().BeNull("es la carga lanzada antes de elegir «No»");
        actual.Consulta.SoloUrgentes.Should().BeTrue("los filtros de la carga vigente se capturan todos");
        actual.Consulta.NotificadoCliente.Should().BeFalse();

        await cut.InvokeAsync(() => actual.Respuesta.SetResult(Pagina()));
        await cambioNotificado.WaitAsync(TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ninguna visita con estos filtros"));

        await cut.InvokeAsync(() => anterior.Respuesta.SetResult(Pagina(Visita("Planta Zaragoza"), Visita("Nave Berriz"))));
        await cambioUrgentes.WaitAsync(TimeSpan.FromSeconds(5));
        mediator.CargasPendientes.Should().HaveCount(5, "detrás de la respuesta tardía no sale ninguna carga que la cure");

        cut.Markup.Should().Contain("Ninguna visita con estos filtros",
            "la respuesta del filtro anterior llegó la última y no puede sustituir a la del filtro vigente");
        cut.Markup.Should().NotContain("2 visitas");
    }

    /// <summary>
    /// Abrir el detalle de una visita y, antes de que cargue, el de otra: la
    /// respuesta lenta de la primera no puede pintar la visita equivocada.
    /// </summary>
    [Fact]
    public async Task Un_detalle_que_llega_tarde_no_sustituye_al_de_la_visita_abierta_despues()
    {
        var norte = Visita("Centro Norte");
        var zaragoza = Visita("Planta Zaragoza");
        var mediator = new MediatorVisitas();
        mediator.Visitas.AddRange([norte, zaragoza]);
        var detalleNorte = new TaskCompletionSource<DetalleVisitaDto?>();
        var detalleZaragoza = new TaskCompletionSource<DetalleVisitaDto?>();
        mediator.DetallesDiferidos[norte.Id] = detalleNorte;
        mediator.DetallesDiferidos[zaragoza.Id] = detalleZaragoza;
        var cut = Renderizar(mediator);

        // Click() no espera al manejador: las dos aperturas quedan en vuelo.
        ItemDeMenu(cut, "Centro Norte", "Ver").Click();
        ItemDeMenu(cut, "Planta Zaragoza", "Ver").Click();

        await cut.InvokeAsync(() => detalleZaragoza.SetResult(MediatorVisitas.Detalle(zaragoza)));
        cut.WaitForAssertion(() => cut.Find(".visitas-detalle-centro").TextContent.Trim().Should().Be("Planta Zaragoza"));

        await cut.InvokeAsync(() => detalleNorte.SetResult(MediatorVisitas.Detalle(norte)));
        // Barrera: el menú de la primera fila se cierra cuando SU manejador
        // termina, así que a partir de aquí la respuesta tardía ya se procesó.
        cut.WaitForAssertion(() => cut.FindAll(".menu-acciones-panel").Should().BeEmpty());

        cut.Find(".visitas-detalle-centro").TextContent.Trim().Should().Be("Planta Zaragoza",
            "la visita que el usuario abrió la última es la que tiene que seguir en el detalle");
    }

    /// <summary>
    /// Mismo caso en el formulario de edición: una edición que llega tarde no
    /// puede rellenar el drawer que ya muestra otra visita.
    /// </summary>
    [Fact]
    public async Task Una_edicion_que_llega_tarde_no_rellena_el_formulario_de_otra_visita()
    {
        var norte = Visita("Centro Norte");
        var zaragoza = Visita("Planta Zaragoza");
        var mediator = new MediatorVisitas();
        mediator.Visitas.AddRange([norte, zaragoza]);
        var edicionNorte = new TaskCompletionSource<VisitaDetalleDto?>();
        mediator.EdicionesDiferidas[norte.Id] = edicionNorte;
        var cut = Renderizar(mediator);

        ItemDeMenu(cut, "Centro Norte", "Editar").Click();
        await ItemDeMenu(cut, "Planta Zaragoza", "Editar").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Find(".drawer-panel").TextContent.Should().Contain("Planta Zaragoza"));

        await cut.InvokeAsync(() => edicionNorte.SetResult(MediatorVisitas.ParaEditar(norte)));
        cut.WaitForAssertion(() => cut.FindAll(".menu-acciones-panel").Should().BeEmpty());

        var formulario = cut.Find(".drawer-panel").TextContent;
        formulario.Should().Contain("Planta Zaragoza");
        formulario.Should().NotContain("Centro Norte", "la respuesta tardía era de otra visita");
    }

    [Fact]
    public async Task El_interruptor_de_la_fila_envia_el_comando_de_esa_visita_y_la_lista_recargada_lo_refleja()
    {
        var norte = Visita("Centro Norte");
        var zaragoza = Visita("Planta Zaragoza");
        var mediator = new MediatorVisitas();
        mediator.Visitas.AddRange([norte, zaragoza]);
        var cut = Renderizar(mediator);
        Interruptor(cut, "Planta Zaragoza").HasAttribute("checked").Should().BeFalse("punto de partida");

        await Interruptor(cut, "Planta Zaragoza").ChangeAsync(new ChangeEventArgs { Value = true });

        mediator.Comandos.Should().ContainSingle().Which.Should().Be(new MarcarNotificadoClienteCommand(zaragoza.Id, true));
        cut.WaitForAssertion(() => Interruptor(cut, "Planta Zaragoza").HasAttribute("checked").Should().BeTrue(
            "la fila se pinta con lo que devuelve la recarga, no con el clic"));
        Interruptor(cut, "Centro Norte").HasAttribute("checked").Should().BeFalse();
        Interruptor(cut, "Planta Zaragoza").GetAttribute("role").Should().Be("switch");
        Interruptor(cut, "Planta Zaragoza").GetAttribute("aria-label").Should().StartWith("Notificada a la empresa titular: Planta Zaragoza");
    }

    [Fact]
    public async Task El_interruptor_de_la_fila_envia_false_al_quitar_la_marca()
    {
        var norte = Visita("Centro Norte", notificado: true);
        var mediator = new MediatorVisitas();
        mediator.Visitas.Add(norte);
        var cut = Renderizar(mediator);

        await Interruptor(cut, "Centro Norte").ChangeAsync(new ChangeEventArgs { Value = false });

        mediator.Comandos.Should().ContainSingle().Which.Should()
            .Be(new MarcarNotificadoClienteCommand(norte.Id, false));
    }

    [Fact]
    public async Task Un_doble_clic_en_el_interruptor_mientras_el_comando_esta_en_vuelo_envia_un_solo_comando()
    {
        var norte = Visita("Centro Norte");
        var mediator = new MediatorVisitas
        {
            NotificacionDiferida = new TaskCompletionSource<Result>(),
        };
        mediator.Visitas.Add(norte);
        var cut = Renderizar(mediator);

        var primerClic = Interruptor(cut, "Centro Norte").ChangeAsync(new ChangeEventArgs { Value = true });
        cut.WaitForAssertion(() => mediator.Comandos.Should().ContainSingle());

        // El segundo clic NO se espera antes de comprobar: sin la guarda, su
        // comando también queda retenido y un await aquí colgaría el test en
        // vez de dejarlo en rojo. El doble registra el comando al recibirlo,
        // así que un segundo envío se vería en este mismo instante.
        var segundoClic = Interruptor(cut, "Centro Norte").ChangeAsync(new ChangeEventArgs { Value = true });
        mediator.Comandos.Should().ContainSingle("la segunda interacción llega mientras el primer comando sigue pendiente");

        await cut.InvokeAsync(() => mediator.NotificacionDiferida!.SetResult(Result.Exito()));
        await Task.WhenAll(primerClic, segundoClic);
    }

    [Fact]
    public async Task Una_excepcion_al_notificar_recarga_la_lista()
    {
        var norte = Visita("Centro Norte");
        var mediator = new MediatorVisitas
        {
            ErrorNotificacion = new InvalidOperationException("fallo del comando"),
        };
        mediator.Visitas.Add(norte);
        var cut = Renderizar(mediator);
        var consultasAntes = mediator.ConsultasVisitas;

        await Interruptor(cut, "Centro Norte").ChangeAsync(new ChangeEventArgs { Value = true });

        mediator.Comandos.Should().ContainSingle();
        mediator.ConsultasVisitas.Should().BeGreaterThan(consultasAntes,
            "tras una excepción se consulta de nuevo el estado real de la lista");
    }

    [Fact]
    public async Task Desde_el_detalle_se_marca_como_notificada_y_la_fila_recargada_lo_refleja()
    {
        var norte = Visita("Centro Norte", notificado: false);
        var mediator = new MediatorVisitas();
        mediator.Visitas.Add(norte);
        var cut = Renderizar(mediator);

        await ItemDeMenu(cut, "Centro Norte", "Ver").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".drawer-pie button").First(b => b.TextContent.Contains("Marcar como notificada"))
            .ClickAsync(new MouseEventArgs());

        mediator.Comandos.Should().ContainSingle().Which.Should().Be(new MarcarNotificadoClienteCommand(norte.Id, true));
        cut.WaitForAssertion(() => Interruptor(cut, "Centro Norte").HasAttribute("checked").Should().BeTrue(
            "tras el comando la lista se recarga y la fila ve el cambio"));
        cut.Find(".drawer-pie").TextContent.Should().Contain("Quitar la marca de notificada");
    }

    [Fact]
    public async Task Eliminar_pide_confirmacion_y_solo_al_confirmar_borra_esa_visita()
    {
        var norte = Visita("Centro Norte");
        var zaragoza = Visita("Planta Zaragoza");
        var mediator = new MediatorVisitas();
        mediator.Visitas.AddRange([norte, zaragoza]);
        var cut = Renderizar(mediator);

        await ItemDeMenu(cut, "Planta Zaragoza", "Eliminar").ClickAsync(new MouseEventArgs());

        cut.Markup.Should().Contain("¿Eliminar la visita a Planta Zaragoza?");
        mediator.Comandos.Should().BeEmpty("abrir el diálogo no borra nada");

        await cut.FindAll(".modal-pie button").First(b => b.TextContent.Contains("Eliminar")).ClickAsync(new MouseEventArgs());

        mediator.Comandos.Should().ContainSingle().Which.Should().Be(new EliminarVisitaCommand(zaragoza.Id));
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().NotContain(tr => tr.TextContent.Contains("Planta Zaragoza")));
        cut.FindAll("tbody tr").Should().Contain(tr => tr.TextContent.Contains("Centro Norte"));
    }

    /// <summary>
    /// La comprobación previa se deriva de la documentación que ya trae la
    /// query: cuenta quién tiene algo pendiente y los pone primero, del peor
    /// estado al mejor, en vez del orden por nombre que llega.
    /// </summary>
    [Fact]
    public async Task La_comprobacion_previa_resume_los_pendientes_y_los_pone_primero()
    {
        var norte = Visita("Centro Norte");
        var mediator = new MediatorVisitas
        {
            Documentacion = new DocumentacionVisitaDto(Guid.NewGuid(),
                new SeccionDocumentacionDto(EstadoDocumento.Vigente, []),
                [
                    Trabajador("Ana Loredo", EstadoDocumento.Vigente),
                    Trabajador("Bruno Salas", EstadoDocumento.Vencido),
                    Trabajador("Carla Vila", EstadoDocumento.Faltante),
                ]),
        };
        mediator.Visitas.Add(norte);
        var cut = Renderizar(mediator);

        await ItemDeMenu(cut, "Centro Norte", "Ver").ClickAsync(new MouseEventArgs());

        cut.Find(".visitas-detalle-resumen").TextContent.Should()
            .Be("2 de 3 trabajadores tienen documentación pendiente para este centro.");

        var trabajadores = cut.FindAll(".seccion-colapsable-titulo")
            .Select(t => t.TextContent)
            .Where(t => t.Contains("(12345678Z)"))
            .ToList();
        trabajadores.Should().HaveCount(3);
        trabajadores[0].Should().StartWith("Carla Vila", "Falta es el peor estado");
        trabajadores[1].Should().StartWith("Bruno Salas");
        trabajadores[2].Should().StartWith("Ana Loredo", "quien está en regla va al final");

        static TrabajadorDocumentacionDto Trabajador(string nombre, EstadoDocumento peor) =>
            new(Guid.NewGuid(), nombre, "12345678Z", "Instalaciones Arbeko S.L.", new SeccionDocumentacionDto(peor, []));
    }

    /// <summary>
    /// La documentación tiene su propio número de carga. Un segundo
    /// «Reintentar» seguido no se puede dar en la interfaz (mientras recarga,
    /// el botón desaparece), pero sí esto: con un reintento aún en vuelo se
    /// vuelve a abrir el detalle; la carga nueva responde primero y la del
    /// reintento llega tarde. Esa respuesta vieja no puede pintar el detalle.
    /// </summary>
    [Fact]
    public async Task Un_reintento_de_documentacion_que_llega_tarde_no_pisa_la_carga_vigente()
    {
        var norte = Visita("Centro Norte");
        var mediator = new MediatorVisitas { DiferirDocumentacion = true };
        mediator.Visitas.Add(norte);
        var cut = Renderizar(mediator);

        ItemDeMenu(cut, "Centro Norte", "Ver").Click();
        cut.WaitForAssertion(() => mediator.CargasDocumentacionPendientes.Should().HaveCount(1));
        await cut.InvokeAsync(() => mediator.CargasDocumentacionPendientes[0].SetException(new InvalidOperationException()));
        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel button").Should().Contain(b => b.TextContent.Contains("Reintentar")));

        // Cada elemento se busca de nuevo antes de pulsarlo: tras un render,
        // el manejador de una referencia anterior ya no existe.
        var reintento = cut.FindAll(".drawer-panel button").First(b => b.TextContent.Contains("Reintentar")).ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => mediator.CargasDocumentacionPendientes.Should().HaveCount(2));

        ItemDeMenu(cut, "Centro Norte", "Ver").Click();
        cut.WaitForAssertion(() => mediator.CargasDocumentacionPendientes.Should().HaveCount(3));

        await cut.InvokeAsync(() => mediator.CargasDocumentacionPendientes[2].SetResult(DocumentacionDe("Última respuesta")));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Última respuesta"));

        await cut.InvokeAsync(() => mediator.CargasDocumentacionPendientes[1].SetResult(DocumentacionDe("Respuesta antigua")));
        await reintento;
        cut.Markup.Should().Contain("Última respuesta");
        cut.Markup.Should().NotContain("Respuesta antigua",
            "la respuesta del reintento ya no es la carga vigente");

        static DocumentacionVisitaDto DocumentacionDe(string nombre) => new(
            Guid.NewGuid(),
            new SeccionDocumentacionDto(EstadoDocumento.Vigente, []),
            [new TrabajadorDocumentacionDto(Guid.NewGuid(), nombre, "12345678Z", "Empresa", new SeccionDocumentacionDto(EstadoDocumento.Vigente, []))]);
    }

    [Fact]
    public void La_urgencia_normal_tambien_se_pinta()
    {
        var mediator = new MediatorVisitas();
        mediator.Visitas.Add(Visita("Nave Berriz", urgencia: NivelUrgenciaVisita.Normal));
        var cut = Renderizar(mediator);

        Fila(cut, "Nave Berriz").TextContent.Should().Contain("Normal",
            "una celda vacía no distingue «normal» de «sin calcular»");
    }

    /// <summary>
    /// Los datos dicen <c>Cliente</c>, pero son la empresa titular del centro.
    /// Ningún texto visible —ni placeholder, ni etiqueta accesible— dice
    /// «cliente» a secas, ni en la lista, ni en el detalle, ni en el formulario.
    /// </summary>
    [Fact]
    public async Task Ningun_texto_visible_dice_cliente_a_secas()
    {
        var mediator = new MediatorVisitas { Tramo = TramoAntelacion.Urgente };
        mediator.Visitas.Add(Visita("Centro Norte"));
        var cut = Renderizar(mediator);
        var cliente = new Regex(@"\bcliente\b", RegexOptions.IgnoreCase);

        cliente.IsMatch(cut.Markup).Should().BeFalse("en la lista");

        await ItemDeMenu(cut, "Centro Norte", "Ver").ClickAsync(new MouseEventArgs());
        cut.Markup.Should().Contain("Antelación", "el bloque de antelación tiene que estar pintado para que esto mida algo");
        cliente.IsMatch(cut.Markup).Should().BeFalse("en el detalle");

        await cut.FindAll(".acciones-cabecera button").First(b => b.TextContent.Contains("Nueva visita")).ClickAsync(new MouseEventArgs());
        cut.Markup.Should().Contain("Notificada a la empresa titular del centro", "el formulario tiene que estar abierto");
        cliente.IsMatch(cut.Markup).Should().BeFalse("en el formulario");
    }

    [Fact]
    public async Task La_anticipo_no_atribuye_el_aviso_a_la_empresa_titular()
    {
        var mediator = new MediatorVisitas { Tramo = TramoAntelacion.Urgente };
        mediator.Visitas.Add(Visita("Centro Norte"));
        var cut = Renderizar(mediator);

        await ItemDeMenu(cut, "Centro Norte", "Ver").ClickAsync(new MouseEventArgs());

        var textoAntelacion = cut.FindAll(".texto-vacio-seccion")
            .First(p => p.TextContent.Contains("Aviso recibido")).TextContent;
        textoAntelacion.Should().NotContain("empresa titular");
    }
}
