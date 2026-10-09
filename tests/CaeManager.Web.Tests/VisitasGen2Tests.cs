using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Infrastructure.Identity;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Centros.Queries.ObtenerTrabajadoresAsignadosDeCentro;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.EnviarMensajeNuevo;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Application.Visitas.Commands.CancelarVisita;
using CaeManager.Application.Visitas.Commands.CancelarVisitas;
using CaeManager.Application.Visitas.Commands.ReactivarVisita;
using CaeManager.Application.Visitas.Commands.MarcarNotificadoCliente;
using CaeManager.Application.Visitas.Queries.ObtenerAvisoVisita;
using CaeManager.Application.Visitas.Queries.ObtenerDetalleVisita;
using CaeManager.Application.Visitas.Queries.ObtenerDocumentacionVisita;
using CaeManager.Application.Visitas.Queries.ObtenerPaqueteDocumentalVisita;
using CaeManager.Application.Visitas.Queries.ObtenerSolicitudAccesoCorreo;
using CaeManager.Application.Visitas.Queries.ObtenerVisitaPorId;
using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Integraciones;
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
    public VisitasGen2Tests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        // Los textos de Visitas salen de IStringLocalizer<TextosVisitas>.
        Services.AddLocalization();
        this.ConRolDeEscritura();
    }

    private static readonly DateOnly Hoy = DiaDeNegocio.Hoy();

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

        public IReadOnlyList<TrabajadorSelectorDto> TrabajadoresSelector { get; set; } = [];

        public IReadOnlyList<CentroSelectorDto> CentrosSelector { get; set; } = [];

        public Dictionary<Guid, Guid[]> AsignadosPorCentro { get; } = [];

        public Dictionary<Guid, TaskCompletionSource> AsignadosRetenidos { get; } = [];

        public bool FallarAsignados { get; set; }

        public List<object> Comandos { get; } = [];

        public int ConsultasVisitas { get; private set; }

        public TaskCompletionSource<Result>? NotificacionDiferida { get; set; }

        public Exception? ErrorNotificacion { get; set; }

        public static DetalleVisitaDto Detalle(VisitaListaDto v, TramoAntelacion? tramo = null) => new(
            v.Id, v.CentroNombre, v.ClienteRazonSocial, v.EmpresaId, v.EmpresaRazonSocial, v.FechaInicio, v.FechaFin,
            Notas: null, v.NotificadoCliente, Trabajadores: [], HoraEstimadaAcceso: null, FechaHoraSolicitudUtc: null,
            FechaHoraExpedienteCompletoUtc: null, AntelacionNominalHoras: 36m, AntelacionEfectivaHoras: 11m, tramo,
            AtribucionUrgencia.SinUrgencia, CentroRequiereGestionCae: v.CentroRequiereGestionCae,
            EstaCancelada: v.EstaCancelada, MotivoCancelacion: v.MotivoCancelacion, Version: v.Version);

        public HashSet<Guid> VisitasPorCorreo { get; } = [];

        public SolicitudAccesoCorreoDto Solicitud { get; set; } = new("acceso@centronorte.es", "Solicitud de acceso — Centro Norte — 01/10/2026", "Buenos días, Marta:\n\n- Ana Garcia (Contratista Demo SL)");

        public int ConsultasSolicitud { get; private set; }

        /// <summary>Lo que devuelve la consulta del paquete documental (la misma que sirve la descarga del ZIP).</summary>
        public Result<PaqueteDocumentalDescargaDto> Paquete { get; set; } =
            Result.Exito(new PaqueteDocumentalDescargaDto("paquete-centro-norte.zip", new byte[1024]));

        /// <summary>FS-15: el Tenant no tiene ningún buzón de Microsoft 365 conectado desde el que enviar.</summary>
        public bool SinBuzon { get; set; }

        public int ConsultasPaquete { get; private set; }

        public AvisoVisitaDto Aviso { get; set; } = new("Aviso de visita — Almacén Sur — 01/10/2026", "Buenos días:\n\n- Ana Garcia (Contratista Demo SL)");

        public int ConsultasDocumentacion { get; private set; }

        public static VisitaDetalleDto ParaEditar(VisitaListaDto v) => new(
            v.Id, v.CentroId, v.CentroNombre, v.ClienteRazonSocial, v.EmpresaRazonSocial, v.FechaInicio, v.FechaFin,
            TrabajadorIds: [], v.NotificadoCliente, Notas: null, HoraEstimadaAcceso: null, Version: Guid.NewGuid());

        private List<VisitaListaDto> Aplicar(ObtenerVisitasQuery consulta) => Visitas
            .Where(v => consulta.NotificadoCliente is null || v.NotificadoCliente == consulta.NotificadoCliente)
            .Where(v => !consulta.SoloUrgentes || v.NivelUrgencia != NivelUrgenciaVisita.Normal)
            .Where(v => !consulta.SoloActivas || (v.FechaFin >= Hoy && !v.EstaCancelada))
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
                        : Respuesta<TResponse>(Detalle(Visitas.Single(v => v.Id == detalle.Id), Tramo) with { CentroGestionadoPorCorreo = VisitasPorCorreo.Contains(detalle.Id) });

                case ObtenerVisitaPorIdQuery edicion:
                    return EdicionesDiferidas.TryGetValue(edicion.Id, out var edicionDiferida)
                        ? (Task<TResponse>)(object)edicionDiferida.Task
                        : Respuesta<TResponse>(ParaEditar(Visitas.Single(v => v.Id == edicion.Id)));

                case ObtenerSolicitudAccesoCorreoQuery:
                    ConsultasSolicitud++;
                    return Respuesta<TResponse>(Result.Exito(Solicitud));

                case ObtenerPaqueteDocumentalVisitaQuery:
                    ConsultasPaquete++;
                    return Respuesta<TResponse>(Paquete);

                case ObtenerConexionesIntegracionQuery when SinBuzon:
                    return Respuesta<TResponse>((IReadOnlyList<ConexionIntegracionListaDto>)[]);

                case ObtenerConexionesIntegracionQuery:
                    return Respuesta<TResponse>((IReadOnlyList<ConexionIntegracionListaDto>)
                        [new(Guid.NewGuid(), "cae@example.invalid", "CAE Norte", null, null, EstadoConexionIntegracion.Habilitada, DateTime.UtcNow, null, null)]);

                case EnviarMensajeNuevoCommand envio:
                    Comandos.Add(envio);
                    return Respuesta<TResponse>(Result.Exito(Guid.NewGuid()));

                case ObtenerAvisoVisitaQuery:
                    return Respuesta<TResponse>(Result.Exito(Aviso));

                case ObtenerDocumentacionVisitaQuery:
                    ConsultasDocumentacion++;
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

                case CancelarVisitaCommand cancelar:
                    {
                        Comandos.Add(cancelar);
                        var i = Visitas.FindIndex(v => v.Id == cancelar.Id);
                        var versionNueva = Guid.NewGuid();
                        Visitas[i] = Visitas[i] with { EstaCancelada = true, MotivoCancelacion = cancelar.Motivo, Version = versionNueva };
                        return Respuesta<TResponse>(Result.Exito(new VisitaCanceladaDto(cancelar.Id, versionNueva)));
                    }

                case CancelarVisitasCommand lote:
                    {
                        Comandos.Add(lote);
                        var recibos = new List<VisitaCanceladaDto>();
                        foreach (var id in lote.Ids)
                        {
                            var i = Visitas.FindIndex(v => v.Id == id);
                            var versionNueva = Guid.NewGuid();
                            Visitas[i] = Visitas[i] with { EstaCancelada = true, MotivoCancelacion = lote.Motivo, Version = versionNueva };
                            recibos.Add(new VisitaCanceladaDto(id, versionNueva));
                        }

                        return Respuesta<TResponse>(Result.Exito(new ResultadoCancelacionLoteDto(lote.Ids.Count, [], recibos)));
                    }

                case ReactivarVisitaCommand reactivar:
                    {
                        Comandos.Add(reactivar);
                        var i = Visitas.FindIndex(v => v.Id == reactivar.Id);
                        // Como el handler real: ya reactivada = éxito; versión desfasada = conflicto.
                        if (!Visitas[i].EstaCancelada)
                            return Respuesta<TResponse>(Result.Exito());

                        if (Visitas[i].Version != reactivar.VersionEsperada)
                            return Respuesta<TResponse>(Result.Fallo(Error.Crear(
                                ConcurrenciaOptimista.CodigoConflicto, "Esta visita cambió desde que la viste.")));

                        Visitas[i] = Visitas[i] with { EstaCancelada = false, MotivoCancelacion = null, Version = Guid.NewGuid() };
                        return Respuesta<TResponse>(Result.Exito());
                    }

                case ObtenerCentrosParaSelectorQuery:
                    return Respuesta<TResponse>(CentrosSelector);

                case ObtenerTrabajadoresAsignadosDeCentroQuery asignados:
                    {
                        if (FallarAsignados)
                            throw new InvalidOperationException("fallo simulado");

                        var lista = (IReadOnlyList<TrabajadorAsignadoDto>)(AsignadosPorCentro.TryGetValue(asignados.CentroId, out var ids) ? ids : [])
                            .Select(id => new TrabajadorAsignadoDto(id, "x", "y", Hoy)).ToList();
                        if (AsignadosRetenidos.TryGetValue(asignados.CentroId, out var retenido))
                            return retenido.Task.ContinueWith(_ => (TResponse)(object)lista);
                        return Respuesta<TResponse>(lista);
                    }

                case ObtenerTrabajadoresParaSelectorQuery:
                    return Respuesta<TResponse>(TrabajadoresSelector);

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
        string centro, bool notificado = false, NivelUrgenciaVisita urgencia = NivelUrgenciaVisita.Urgente,
        OrigenVisita origen = OrigenVisita.Correo) =>
        new(Guid.NewGuid(), Guid.NewGuid(), centro, Guid.NewGuid(), "Iberojet S.A.", Guid.NewGuid(), "Instalaciones Arbeko S.L.",
            Hoy, Hoy.AddDays(2), TotalTrabajadores: 3, DocumentacionCompleta: false, notificado, origen, urgencia,
            Version: Guid.NewGuid());

    private static ResultadoPaginado<VisitaListaDto> Pagina(params VisitaListaDto[] visitas) =>
        new(visitas, visitas.Length, 1, 20);

    private IRenderedComponent<Visitas> Renderizar(MediatorVisitas mediator)
    {
        Services.AddScoped<IMediator>(_ => mediator);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        return Render<Visitas>();
    }

    /// <summary>
    /// Cambiar el filtro recarga la lista UNA vez.
    /// <c>SetCurrentPageIndexAsync</c> ya avisa a QuickGrid aunque la página no
    /// cambie, así que refrescar además la rejilla pedía lo mismo dos veces.
    /// El primer cambio solo asienta el total en 1; se mide el SEGUNDO, de «No»
    /// a «Sí», con el total quieto — si cambiara, QuickGrid volvería a pedir la
    /// misma página por su cuenta y el recuento mezclaría esa repetición.
    /// </summary>
    [Fact]
    public async Task Cambiar_de_filtro_sin_cambiar_el_total_hace_una_sola_consulta()
    {
        var mediator = new MediatorVisitas { Visitas = { Visita("Centro Norte"), Visita("Planta Zaragoza", notificado: true) } };
        var cut = Renderizar(mediator);

        await cut.Find(".barra-filtros select").ChangeAsync(new ChangeEventArgs { Value = "no" });
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Centro Norte").And.NotContain("Planta Zaragoza"));
        var consultasAntes = mediator.ConsultasVisitas;

        await cut.Find(".barra-filtros select").ChangeAsync(new ChangeEventArgs { Value = "si" });

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Planta Zaragoza").And.NotContain("Centro Norte"));
        (mediator.ConsultasVisitas - consultasAntes).Should().Be(1,
            "los dos valores devuelven una visita: la única consulta que cabe contar es la del filtro nuevo");
    }

    /// <summary>
    /// Cambiar el tamaño de página pide la página 1 del tamaño nuevo UNA vez,
    /// por el mismo motivo; las dos visitas son las mismas antes y después.
    /// </summary>
    [Fact]
    public async Task Cambiar_el_tamano_de_pagina_hace_una_sola_consulta()
    {
        var mediator = new MediatorVisitas { Visitas = { Visita("Centro Norte"), Visita("Planta Zaragoza") } };
        var cut = Renderizar(mediator);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Centro Norte"));
        var consultasAntes = mediator.ConsultasVisitas;

        await cut.Find(".paginador-tamano-select").ChangeAsync(new ChangeEventArgs { Value = "50" });

        cut.WaitForAssertion(() => mediator.ConsultasVisitas.Should().BeGreaterThan(consultasAntes));
        (mediator.ConsultasVisitas - consultasAntes).Should().Be(1,
            "avisar a la paginación y refrescar la rejilla son dos formas de pedir lo mismo");
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

        // Medido, no supuesto: cada cambio de filtro pide UNA carga. Antes pedía
        // dos, porque RecargarAsync avisaba a la paginación Y refrescaba la
        // rejilla, que son dos formas de pedir lo mismo; este test se escribió
        // sobre aquella coreografía de dos en dos. La carrera que mide no
        // dependía de eso y sigue igual: la carga del filtro ANTERIOR llegando
        // después de todo lo del filtro nuevo, sin que nada vuelva a cargar
        // detrás de ella para curarla.
        //
        // Las tareas de los manejadores se guardan sin esperar y se esperan al
        // final como barrera: la primera versión de este test comprobaba antes
        // de que la respuesta tardía se aplicara, y dio verde con la guarda
        // quitada (mutación M1).
        var cambioUrgentes = FiltroCheckbox(cut, "Solo urgentes").ChangeAsync(new ChangeEventArgs { Value = true });
        cut.WaitForAssertion(() => mediator.CargasPendientes.Should().HaveCount(2));
        var anterior = mediator.CargasPendientes[1];

        var cambioNotificado = cut.Find(".barra-filtros select").ChangeAsync(new ChangeEventArgs { Value = "no" });
        cut.WaitForAssertion(() => mediator.CargasPendientes.Should().HaveCount(3));
        var actual = mediator.CargasPendientes[2];

        anterior.Consulta.SoloUrgentes.Should().BeTrue();
        anterior.Consulta.NotificadoCliente.Should().BeNull("es la carga lanzada antes de elegir «No»");
        actual.Consulta.SoloUrgentes.Should().BeTrue("los filtros de la carga vigente se capturan todos");
        actual.Consulta.NotificadoCliente.Should().BeFalse();

        // La respuesta vigente trae un total distinto (de 1 a 0) y eso hace que
        // QuickGrid pida la misma página otra vez por su cuenta: su guarda
        // compara un hash de PaginationState que incluye TotalItemCount y que se
        // guardó ANTES de conocer el total nuevo, así que queda rancio. Es del
        // componente, no de la página — y por eso los tests de recuento de
        // consultas se escriben siempre con el total quieto. Se resuelve esa
        // repetición para llegar al estado vacío; con el total ya en 0 no vuelve
        // a repetirse.
        await cut.InvokeAsync(() => actual.Respuesta.SetResult(Pagina()));
        cut.WaitForAssertion(() => mediator.CargasPendientes.Should().HaveCount(4));
        await cut.InvokeAsync(() => mediator.CargasPendientes[3].Respuesta.SetResult(Pagina()));
        await cambioNotificado.WaitAsync(TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ninguna visita con estos filtros"));

        await cut.InvokeAsync(() => anterior.Respuesta.SetResult(Pagina(Visita("Planta Zaragoza"), Visita("Nave Berriz"))));
        await cambioUrgentes.WaitAsync(TimeSpan.FromSeconds(5));
        mediator.CargasPendientes.Should().HaveCount(4, "detrás de la respuesta tardía no sale ninguna carga que la cure");

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
        Interruptor(cut, "Planta Zaragoza").GetAttribute("aria-label").Should().StartWith("Notificada al titular del Centro: Planta Zaragoza");
    }

    /// <summary>
    /// P1-X2: la visita a un Centro sin gestión CAE no tiene documentación que
    /// completar. La columna no dice «Completa» (verde falso) ni «Por
    /// gestionar» (pendiente que no existe): dice que no requiere gestión CAE.
    /// </summary>
    [Fact]
    public void La_fila_de_un_centro_sin_gestion_cae_no_pinta_ni_completa_ni_por_gestionar()
    {
        var mediator = new MediatorVisitas
        {
            Visitas = { Visita("Almacén Sur") with { CentroRequiereGestionCae = false }, Visita("Centro Norte") }
        };
        var cut = Renderizar(mediator);

        cut.WaitForAssertion(() => Fila(cut, "Almacén Sur").TextContent.Should().Contain("No requiere gestión CAE"));
        Fila(cut, "Almacén Sur").TextContent.Should().NotContain("Completa").And.NotContain("Por gestionar");
        Fila(cut, "Almacén Sur").QuerySelectorAll(".badge").Should().NotContain(b => b.ClassList.Contains("badge-exito") || b.ClassList.Contains("badge-peligro"),
            "ni verde ni rojo: el Centro no está al día ni en falta, no se le exige nada");
        Fila(cut, "Centro Norte").TextContent.Should().NotContain("No requiere gestión CAE", "control positivo: el Centro con gestión sigue igual");
    }

    /// <summary>
    /// P1-X2: en el cajón, un Centro sin gestión CAE ofrece el aviso de la
    /// visita para copiar (asunto y cuerpo, tal como los compone Application)
    /// en lugar de la comprobación previa de documentación, que ni se pide.
    /// </summary>
    [Fact]
    public async Task El_cajon_de_un_centro_sin_gestion_cae_ofrece_el_aviso_copiable_y_no_la_documentacion()
    {
        var sur = Visita("Almacén Sur") with { CentroRequiereGestionCae = false };
        var mediator = new MediatorVisitas();
        mediator.Visitas.Add(sur);
        var cut = Renderizar(mediator);

        await ItemDeMenu(cut, "Almacén Sur", "Ver").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Find(".visitas-aviso").TextContent.Should().Contain("Ana Garcia (Contratista Demo SL)"));
        var panel = cut.Find(".drawer-panel").TextContent;
        panel.Should().Contain("Este centro no requiere gestión CAE");
        panel.Should().NotContain("Comprobación previa");
        cut.FindComponents<BotonCopiar>().Should().ContainSingle()
            .Which.Instance.Valor.Should().Be(mediator.Aviso.Asunto + "\n\n" + mediator.Aviso.Cuerpo,
                "lo que se pega en el correo es el asunto y el cuerpo, sin nada añadido por la página");
        mediator.ConsultasDocumentacion.Should().Be(0, "sin gestión CAE no hay documentación que consultar");
    }

    /// <summary>
    /// P1-X1: en el cajón, un Centro gestionado por correo ofrece la solicitud de
    /// acceso para copiar (asunto y cuerpo tal como los compone Application) y el
    /// enlace de descarga del zip, sin quitar la comprobación previa de documentación.
    /// </summary>
    [Fact]
    public async Task El_cajon_de_un_centro_gestionado_por_correo_ofrece_la_solicitud_copiable_y_el_zip()
    {
        var norte = Visita("Centro Norte");
        var mediator = new MediatorVisitas();
        mediator.Visitas.Add(norte);
        mediator.VisitasPorCorreo.Add(norte.Id);
        var cut = Renderizar(mediator);

        await ItemDeMenu(cut, "Centro Norte", "Ver").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Find(".visitas-aviso-destinatarios").TextContent.Should().Contain("acceso@centronorte.es"));
        cut.FindComponents<BotonCopiar>().Should().ContainSingle()
            .Which.Instance.Valor.Should().Be(mediator.Solicitud.Asunto + "\n\n" + mediator.Solicitud.Cuerpo,
                "lo que se pega en el correo es el asunto y el cuerpo, sin nada añadido por la página");
        var descarga = cut.Find($"a[href='/visitas/{norte.Id}/paquete-documental.zip']");
        descarga.HasAttribute("download").Should().BeTrue("el zip se descarga, no se navega a él");
        descarga.GetAttribute("data-enhance-nav").Should().Be("false", "la navegación mejorada de Blazor no debe interceptar la descarga");
        cut.WaitForAssertion(() => mediator.ConsultasDocumentacion.Should().Be(1, "la comprobación previa sigue: el Centro requiere gestión CAE"));
    }


    private async Task<(IRenderedComponent<Visitas> Cut, MediatorVisitas Mediator)> AbrirCajonPorCorreoAsync(Result<PaqueteDocumentalDescargaDto>? paquete = null)
    {
        var norte = Visita("Centro Norte");
        var mediator = new MediatorVisitas();
        if (paquete is not null) mediator.Paquete = paquete;
        mediator.Visitas.Add(norte);
        mediator.VisitasPorCorreo.Add(norte.Id);
        var cut = Renderizar(mediator);
        await ItemDeMenu(cut, "Centro Norte", "Ver").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Find(".visitas-aviso-destinatarios").TextContent.Should().Contain("acceso@centronorte.es"));
        return (cut, mediator);
    }

    private static IElement BotonEnviarPorCorreo(IRenderedComponent<Visitas> cut) =>
        cut.FindAll(".visitas-aviso-acciones button").Single(b => b.TextContent.Trim() == "Enviar por correo");

    /// <summary>
    /// «Enviar por correo»: el compositor llega con el contacto del Centro, el asunto y el cuerpo de la solicitud
    /// y el ZIP que construye la misma consulta que la descarga. Abrirlo no envía nada: el Gestor CAE revisa y pulsa «Enviar».
    /// </summary>
    [Fact]
    public async Task Enviar_por_correo_abre_el_compositor_con_contacto_asunto_cuerpo_y_zip_sin_enviar_nada()
    {
        var (cut, mediator) = await AbrirCajonPorCorreoAsync();

        await BotonEnviarPorCorreo(cut).ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.FindComponents<RedactarMensajeDrawer>().Single().Instance.Visible.Should().BeTrue());
        var campos = cut.FindComponents<CampoTexto>().Select(c => c.Instance.Valor).ToList();
        campos.Should().Contain("acceso@centronorte.es", "el contacto sale del Canal de gestión del Centro que ya lee la solicitud");
        campos.Should().Contain(mediator.Solicitud.Asunto);
        cut.FindComponents<CampoTextarea>().Should().Contain(c => c.Instance.Valor == mediator.Solicitud.Cuerpo);
        cut.Markup.Should().Contain("paquete-centro-norte.zip");
        mediator.ConsultasPaquete.Should().Be(1, "el paquete sale de ObtenerPaqueteDocumentalVisitaQuery, sin reimplementar qué viaja");
        mediator.Comandos.OfType<EnviarMensajeNuevoCommand>().Should().BeEmpty("el correo lo envía el Gestor CAE tras revisar: abrir el compositor no envía nada");
    }

    [Fact]
    public async Task Al_enviar_desde_el_compositor_el_comando_lleva_el_zip_y_el_cuerpo_con_los_saltos_de_linea_en_html()
    {
        var (cut, mediator) = await AbrirCajonPorCorreoAsync();
        await BotonEnviarPorCorreo(cut).ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll(".drawer-pie button").Should().NotBeEmpty());

        await cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == "Enviar").ClickAsync(new MouseEventArgs());

        var envio = mediator.Comandos.OfType<EnviarMensajeNuevoCommand>().Should().ContainSingle().Subject;
        envio.Destinatarios.Should().Equal("acceso@centronorte.es");
        var adjunto = envio.Adjuntos.Should().ContainSingle().Subject;
        adjunto.NombreArchivo.Should().Be("paquete-centro-norte.zip");
        adjunto.TipoContenido.Should().Be("application/zip");
        adjunto.Contenido.Length.Should().Be(1024);
        envio.CuerpoHtml.Should().Contain("<br>", "el cuadro es texto plano: sin esto los párrafos llegarían colapsados")
            .And.NotContain("\n");
    }

    /// <summary>Tope de adjuntos de Graph (3 MB): por encima no se abre el compositor y el aviso dice qué hacer, con la descarga a mano.</summary>
    [Fact]
    public async Task Un_zip_por_encima_del_tope_no_abre_el_compositor_y_ofrece_la_descarga()
    {
        var grande = new byte[(int)LimitesAdjuntosCorreo.TamanoMaximoTotalAdjuntosBytes + 1];
        var (cut, _) = await AbrirCajonPorCorreoAsync(Result.Exito(new PaqueteDocumentalDescargaDto("grande.zip", grande)));

        await BotonEnviarPorCorreo(cut).ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Find(".visitas-solicitud-correo .alerta-formulario").TextContent
            .Should().Contain("3,1 MB").And.Contain("hasta 3 MB").And.Contain("Descárgalo"));
        cut.FindComponents<RedactarMensajeDrawer>().Single().Instance.Visible.Should().BeFalse("el ZIP no cabe en un correo: no se abre un compositor que acabaría fallando al enviar");
        cut.FindAll("a[href$='paquete-documental.zip']").Should().ContainSingle("la descarga sigue disponible como alternativa");
    }

    [Fact]
    public async Task Un_zip_justo_en_el_tope_si_abre_el_compositor()
    {
        var justo = new byte[(int)LimitesAdjuntosCorreo.TamanoMaximoTotalAdjuntosBytes];
        var (cut, _) = await AbrirCajonPorCorreoAsync(Result.Exito(new PaqueteDocumentalDescargaDto("justo.zip", justo)));

        await BotonEnviarPorCorreo(cut).ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.FindComponents<RedactarMensajeDrawer>().Single().Instance.Visible.Should().BeTrue());
    }

    [Fact]
    public async Task Si_el_paquete_falla_el_aviso_dice_el_motivo_y_no_se_abre_el_compositor()
    {
        var (cut, _) = await AbrirCajonPorCorreoAsync(Result.Fallo<PaqueteDocumentalDescargaDto>(ObtenerPaqueteDocumentalVisitaQueryHandler.SinDocumentos));

        await BotonEnviarPorCorreo(cut).ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Find(".visitas-solicitud-correo .alerta-formulario").TextContent.Should().Contain("No hay documentación vigente"));
        cut.FindComponents<RedactarMensajeDrawer>().Single().Instance.Visible.Should().BeFalse();
    }

    /// <summary>
    /// FS-15: en una Visita de un Centro de canal Email, «Enviar por correo» sin buzón de Microsoft 365 conectado no acaba
    /// en un aviso que se va. El compositor se queda abierto con el motivo, a quién pedírselo (el Gestor CAE no puede abrir
    /// Conexiones de integración) y la alternativa de siempre: copiar la solicitud y bajar el mismo ZIP.
    /// </summary>
    [Fact]
    public async Task Enviar_por_correo_sin_buzon_conectado_explica_el_motivo_y_deja_copiar_el_texto_y_bajar_el_zip()
    {
        var (cut, mediator) = await AbrirCajonPorCorreoAsync();
        mediator.SinBuzon = true;

        await BotonEnviarPorCorreo(cut).ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Find(".drawer-panel .aviso-sin-buzon").TextContent.Should().Contain("no hay ningún buzón de Microsoft 365 conectado"));
        cut.FindComponents<RedactarMensajeDrawer>().Single().Instance.Visible.Should().BeTrue("cerrarse solo era el flujo sin salida");
        cut.Find(".aviso-sin-buzon-pedir").TextContent.Should().Contain("rol Administrador de esta organización");
        cut.FindAll("a.aviso-sin-buzon-conectar").Should().BeEmpty("el rol Gestor CAE no abre /integraciones");
        cut.Find(".aviso-sin-buzon").TextContent.Should().Contain("acceso@centronorte.es");
        cut.FindComponent<AvisoSinBuzonCorreo>().FindComponent<BotonCopiar>().Instance.Valor.Should().StartWith(mediator.Solicitud.Asunto + "\n\n");
        cut.Find("a.aviso-sin-buzon-descargar").GetAttribute("href").Should().EndWith("/paquete-documental.zip");
        mediator.Comandos.OfType<EnviarMensajeNuevoCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task Un_centro_sin_canal_de_correo_no_ofrece_enviar_por_correo()
    {
        var norte = Visita("Centro Norte");
        var mediator = new MediatorVisitas();
        mediator.Visitas.Add(norte);
        var cut = Renderizar(mediator);

        await ItemDeMenu(cut, "Centro Norte", "Ver").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => mediator.ConsultasDocumentacion.Should().Be(1));
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Enviar por correo");
    }

    [Fact]
    public async Task El_cajon_de_un_centro_con_gestion_que_no_es_por_correo_no_ofrece_solicitud_ni_zip()
    {
        var norte = Visita("Centro Norte");
        var mediator = new MediatorVisitas();
        mediator.Visitas.Add(norte);
        var cut = Renderizar(mediator);

        await ItemDeMenu(cut, "Centro Norte", "Ver").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => mediator.ConsultasDocumentacion.Should().Be(1));
        cut.FindAll("a[href$='paquete-documental.zip']").Should().BeEmpty();
        cut.FindComponents<BotonCopiar>().Should().BeEmpty();
        mediator.ConsultasSolicitud.Should().Be(0, "sin canal de correo no se compone la solicitud");
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
    public async Task Consulta_ve_la_marca_de_notificada_sin_que_se_le_ofrezca_cambiarla_ni_editar()
    {
        // MarcarNotificadoClienteCommand y EditarVisitaCommand son ICommand que
        // AutorizacionEscrituraBehavior deniega a Consulta. El interruptor de la fila
        // es también el dato: se le pinta deshabilitado, no se le quita.
        this.ConRolDeEscritura(Roles.Consulta);
        var norte = Visita("Centro Norte", notificado: true);
        var mediator = new MediatorVisitas();
        mediator.Visitas.Add(norte);
        var cut = Renderizar(mediator);

        Interruptor(cut, "Centro Norte").HasAttribute("checked").Should().BeTrue("el dato sigue a la vista");
        Interruptor(cut, "Centro Norte").HasAttribute("disabled").Should().BeTrue("pero no se ofrece cambiarlo");

        await ItemDeMenu(cut, "Centro Norte", "Ver").ClickAsync(new MouseEventArgs());

        cut.FindAll(".drawer-pie button").Select(b => b.TextContent.Trim())
            .Should().NotContain(["Quitar la marca de notificada", "Marcar como notificada", "Editar"])
            .And.Equal(["Cerrar"], "el pie no queda vacío");
        mediator.Comandos.Should().BeEmpty();
    }

    [Theory]
    [InlineData(Roles.GestorCae, true)]
    [InlineData(Roles.Consulta, false)]
    public async Task Cancelar_seleccionadas_solo_se_ofrece_a_los_roles_con_escritura(string rol, bool seOfrece)
    {
        // CancelarVisitasCommand es ICommand: a Consulta no se le ofrece la barra del
        // lote. El caso con escritura es el control de que la selección llegó a hacerse.
        this.ConRolDeEscritura(rol);
        var mediator = new MediatorVisitas();
        mediator.Visitas.Add(Visita("Centro Norte"));
        var cut = Renderizar(mediator);

        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Selección múltiple").ClickAsync(new MouseEventArgs());
        await Fila(cut, "Centro Norte").QuerySelector("input[type=checkbox]:not(.visitas-interruptor)")!
            .ChangeAsync(new ChangeEventArgs { Value = true });

        cut.FindAll(".barra-acciones-lote button").Any(b => b.TextContent.Trim() == "Cancelar seleccionadas")
            .Should().Be(seOfrece);
    }

    /// <summary>
    /// FS-11: cancelar pide confirmación con motivo opcional, deja la Visita fuera de la
    /// lista activa y el aviso ofrece «Deshacer», que la reactiva.
    /// </summary>
    [Fact]
    public async Task Cancelar_pide_confirmacion_con_motivo_y_el_aviso_la_deshace()
    {
        var norte = Visita("Centro Norte");
        var zaragoza = Visita("Planta Zaragoza");
        var mediator = new MediatorVisitas();
        mediator.Visitas.AddRange([norte, zaragoza]);
        var cut = Renderizar(mediator);

        await ItemDeMenu(cut, "Planta Zaragoza", "Cancelar visita").ClickAsync(new MouseEventArgs());

        cut.Markup.Should().Contain("¿Cancelar la visita a Planta Zaragoza?");
        cut.Markup.Should().Contain("se conserva en el historial");
        mediator.Comandos.Should().BeEmpty("abrir el diálogo no cancela nada");

        await cut.Find("[role=dialog] textarea").InputAsync(new ChangeEventArgs { Value = "Obra aplazada" });
        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Cancelar visita").ClickAsync(new MouseEventArgs());

        mediator.Comandos.Should().ContainSingle().Which.Should().Be(new CancelarVisitaCommand(zaragoza.Id, "Obra aplazada"));
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().NotContain(tr => tr.TextContent.Contains("Planta Zaragoza")));
        cut.FindAll("tbody tr").Should().Contain(tr => tr.TextContent.Contains("Centro Norte"));

        var aviso = Services.GetRequiredService<ToastService>().Mensajes.Single(m => m.TextoAccion == "Deshacer");
        await cut.InvokeAsync(aviso.OnAccion!);

        mediator.Comandos.OfType<ReactivarVisitaCommand>().Should().ContainSingle().Which.Id.Should().Be(zaragoza.Id);
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().Contain(tr => tr.TextContent.Contains("Planta Zaragoza")));
    }

    /// <summary>
    /// FS-11: en el historial una Visita cancelada se marca como tal y ofrece
    /// «Reactivar», no «Editar» ni «Cancelar visita», a los mismos roles que pueden
    /// cancelar (los de escritura); a Consulta, no.
    /// </summary>
    [Theory]
    [InlineData(Roles.GestorCae, true)]
    [InlineData(Roles.CoordinadorCae, true)]
    [InlineData(Roles.DireccionCae, true)]
    [InlineData(Roles.Administrador, true)]
    [InlineData(Roles.Consulta, false)]
    public async Task Una_visita_cancelada_se_marca_y_ofrece_reactivar_solo_a_quien_puede(string rol, bool seOfrece)
    {
        this.ConRolDeEscritura(rol);
        var cancelada = Visita("Planta Zaragoza") with { EstaCancelada = true, MotivoCancelacion = "Obra aplazada" };
        var mediator = new MediatorVisitas();
        mediator.Visitas.Add(cancelada);
        var cut = Renderizar(mediator);
        await cut.FindAll("input[type=checkbox]").First(c => c.ParentElement!.TextContent.Contains("Solo activas"))
            .ChangeAsync(new ChangeEventArgs { Value = false });

        var fila = Fila(cut, "Planta Zaragoza");
        fila.TextContent.Should().Contain("Cancelada");
        fila.QuerySelector("[title='Obra aplazada']").Should().NotBeNull("el motivo acompaña a la marca");
        fila.QuerySelector(".menu-acciones-disparador")!.Click();
        var acciones = Fila(cut, "Planta Zaragoza").QuerySelectorAll(".menu-acciones-item").Select(e => e.TextContent.Trim()).ToList();
        acciones.Should().Contain("Ver", "control positivo: el menú se abrió");
        acciones.Should().NotContain(["Editar", "Cancelar visita"]);
        acciones.Contains("Reactivar").Should().Be(seOfrece);
    }

    /// <summary>
    /// FS-11: la ficha de una Visita cancelada no carga ni ofrece acciones
    /// operativas (solicitud de acceso, paquete, comprobación previa): solo reactivar.
    /// </summary>
    [Fact]
    public async Task La_ficha_de_una_visita_cancelada_no_ofrece_acciones_operativas()
    {
        this.ConRolDeEscritura(Roles.GestorCae);
        var cancelada = Visita("Planta Zaragoza") with { EstaCancelada = true, MotivoCancelacion = "Obra aplazada" };
        var mediator = new MediatorVisitas();
        mediator.Visitas.Add(cancelada);
        mediator.VisitasPorCorreo.Add(cancelada.Id);
        var cut = Renderizar(mediator);
        await cut.FindAll("input[type=checkbox]").First(c => c.ParentElement!.TextContent.Contains("Solo activas"))
            .ChangeAsync(new ChangeEventArgs { Value = false });

        Interruptor(cut, "Planta Zaragoza").HasAttribute("disabled").Should().BeTrue("una cancelada no se marca como notificada");
        await ItemDeMenu(cut, "Planta Zaragoza", "Ver").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Visita cancelada. Motivo: Obra aplazada"));
        cut.Markup.Should().Contain("Reactívala para retomarla");
        cut.FindAll(".drawer-pie button").Select(b => b.TextContent.Trim()).Should().Equal(["Reactivar", "Cerrar"]);
        mediator.ConsultasSolicitud.Should().Be(0, "no se compone la solicitud de acceso de una visita cancelada");
    }

    [Fact]
    public async Task Reactivar_pide_confirmacion_con_motivo_opcional_y_manda_el_comando()
    {
        this.ConRolDeEscritura(Roles.GestorCae);
        var cancelada = Visita("Planta Zaragoza") with { EstaCancelada = true };
        var mediator = new MediatorVisitas();
        mediator.Visitas.Add(cancelada);
        var cut = Renderizar(mediator);
        await cut.FindAll("input[type=checkbox]").First(c => c.ParentElement!.TextContent.Contains("Solo activas"))
            .ChangeAsync(new ChangeEventArgs { Value = false });

        await ItemDeMenu(cut, "Planta Zaragoza", "Reactivar").ClickAsync(new MouseEventArgs());
        cut.Markup.Should().Contain("¿Reactivar la visita a Planta Zaragoza?");
        await cut.Find("[role=dialog] textarea").InputAsync(new ChangeEventArgs { Value = "Se retoma" });
        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Reactivar").ClickAsync(new MouseEventArgs());

        mediator.Comandos.Should().ContainSingle().Which.Should().Be(new ReactivarVisitaCommand(cancelada.Id, cancelada.Version, "Se retoma"));
    }

    /// <summary>
    /// Versión desfasada al reactivar (otra persona cambió la Visita desde que se cargó la lista): el aviso lo
    /// dice, el diálogo se cierra y la lista se recarga con la versión nueva; no hay pérdida silenciosa y
    /// reactivar de nuevo, ya con la versión recargada, funciona.
    /// </summary>
    [Fact]
    public async Task Reactivar_con_version_desfasada_avisa_recarga_y_permite_reintentar()
    {
        this.ConRolDeEscritura(Roles.GestorCae);
        var cancelada = Visita("Planta Zaragoza") with { EstaCancelada = true };
        var mediator = new MediatorVisitas();
        mediator.Visitas.Add(cancelada);
        var cut = Renderizar(mediator);
        await cut.FindAll("input[type=checkbox]").First(c => c.ParentElement!.TextContent.Contains("Solo activas"))
            .ChangeAsync(new ChangeEventArgs { Value = false });
        await ItemDeMenu(cut, "Planta Zaragoza", "Reactivar").ClickAsync(new MouseEventArgs());

        // Otra persona toca la Visita mientras el diálogo está abierto.
        var versionNueva = Guid.NewGuid();
        mediator.Visitas[0] = mediator.Visitas[0] with { Version = versionNueva };
        var consultasAntes = mediator.ConsultasVisitas;

        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Reactivar").ClickAsync(new MouseEventArgs());

        mediator.Comandos.OfType<ReactivarVisitaCommand>().Should().ContainSingle().Which.VersionEsperada.Should().Be(cancelada.Version);
        Services.GetRequiredService<ToastService>().Mensajes.Should().Contain(m => m.Mensaje.Contains("cambió"));
        mediator.Visitas[0].EstaCancelada.Should().BeTrue("el conflicto no reactivó nada");
        cut.Markup.Should().NotContain("¿Reactivar la visita a Planta Zaragoza?", "el diálogo se cierra");
        mediator.ConsultasVisitas.Should().BeGreaterThan(consultasAntes, "se recarga para ver lo que hay");

        await ItemDeMenu(cut, "Planta Zaragoza", "Reactivar").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Reactivar").ClickAsync(new MouseEventArgs());

        mediator.Comandos.OfType<ReactivarVisitaCommand>().Last().VersionEsperada.Should().Be(versionNueva);
        mediator.Visitas[0].EstaCancelada.Should().BeFalse();
    }

    /// <summary>
    /// «Deshacer» tras cancelar manda la versión del recibo de la cancelación, no la que la lista tenga después:
    /// si alguien cambió la Visita entretanto, el aviso dice el motivo y la Visita sigue cancelada.
    /// </summary>
    [Fact]
    public async Task Deshacer_cancelar_manda_la_version_del_recibo_y_avisa_si_la_visita_cambio()
    {
        this.ConRolDeEscritura(Roles.GestorCae);
        var zaragoza = Visita("Planta Zaragoza");
        var mediator = new MediatorVisitas();
        mediator.Visitas.Add(zaragoza);
        var cut = Renderizar(mediator);

        await ItemDeMenu(cut, "Planta Zaragoza", "Cancelar visita").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Cancelar visita").ClickAsync(new MouseEventArgs());
        var versionDelRecibo = mediator.Visitas[0].Version;

        // Otra persona cambia la Visita antes de que se pulse «Deshacer».
        mediator.Visitas[0] = mediator.Visitas[0] with { Version = Guid.NewGuid() };
        var aviso = Services.GetRequiredService<ToastService>().Mensajes.Single(m => m.TextoAccion == "Deshacer");
        await cut.InvokeAsync(aviso.OnAccion!);

        mediator.Comandos.OfType<ReactivarVisitaCommand>().Should().ContainSingle().Which.VersionEsperada.Should().Be(versionDelRecibo);
        mediator.Visitas[0].EstaCancelada.Should().BeTrue("el rechazo por versión no pisa el cambio ajeno");
        Services.GetRequiredService<ToastService>().Mensajes.Should().Contain(m => m.Mensaje.Contains("cambió"));
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
                    Trabajador("Bruno Salas", EstadoDocumento.Faltante),
                    Trabajador("Carla Vila", EstadoDocumento.Vencido),
                ]),
        };
        mediator.Visitas.Add(norte);
        var cut = Renderizar(mediator);

        await ItemDeMenu(cut, "Centro Norte", "Ver").ClickAsync(new MouseEventArgs());

        cut.Find(".visitas-detalle-resumen").TextContent.Should()
            .Be("2 de 3 trabajadores tienen documentación pendiente para este centro.");

        // Decisión del 2026-09-24 (DNI residual, S1): el título es «Nombre — Empleador», sin DNI.
        // La igualdad exacta es la que pone esto en rojo si el DNI vuelve al título.
        var trabajadores = cut.FindAll(".seccion-colapsable-titulo")
            .Select(t => t.TextContent.Trim())
            .Where(t => t.Contains("Instalaciones Arbeko S.L."))
            .ToList();
        trabajadores.Should().Equal(
            ["Carla Vila — Instalaciones Arbeko S.L.",
             "Bruno Salas — Instalaciones Arbeko S.L.",
             "Ana Loredo — Instalaciones Arbeko S.L."],
            "orden de gravedad (decisión del 2026-10-03): Vencido va antes que Faltante (antes era al revés), quien está en regla va al final y el título no lleva el DNI");

        static TrabajadorDocumentacionDto Trabajador(string nombre, EstadoDocumento peor) =>
            new(Guid.NewGuid(), nombre, "Instalaciones Arbeko S.L.", new SeccionDocumentacionDto(peor, []));
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
            [new TrabajadorDocumentacionDto(Guid.NewGuid(), nombre, "Empresa", new SeccionDocumentacionDto(EstadoDocumento.Vigente, []))]);
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
        cut.Markup.Should().Contain("Marcar como avisado al titular del Centro", "el formulario tiene que estar abierto");
        cliente.IsMatch(cut.Markup).Should().BeFalse("en el formulario");
    }

    /// <summary>
    /// P4 (2026-09-23): «Trabajadores que entran» ofrece la base general del Tenant sin DNI. El fake
    /// siembra un DNI conocido en el origen (<see cref="TrabajadorSelectorFalso"/>).
    /// </summary>
    [Fact]
    public async Task El_selector_de_trabajadores_que_entran_no_muestra_el_DNI()
    {
        var mediator = new MediatorVisitas
        {
            TrabajadoresSelector = [TrabajadorSelectorFalso.Crear(Guid.NewGuid(), "Iker Zubiri Olano")],
        };
        var cut = Renderizar(mediator);

        await cut.FindAll(".acciones-cabecera button").First(b => b.TextContent.Contains("Nueva visita")).ClickAsync(new MouseEventArgs());

        var selector = cut.FindComponents<SelectorMultiple>().Single();
        selector.Instance.Elementos.Select(e => e.Nombre).Should().Equal("Iker Zubiri Olano");
        cut.Markup.Should().Contain("Iker Zubiri Olano").And.NotContain(TrabajadorSelectorFalso.DniSembrado);
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

    private async Task AbrirNuevaVisitaAsync(IRenderedComponent<Visitas> cut) =>
        await cut.FindAll(".acciones-cabecera button").First(b => b.TextContent.Contains("Nueva visita")).ClickAsync(new MouseEventArgs());

    /// <summary>
    /// P1-E2: con el drawer de alta a medias (unas notas escritas), salir por la aplicación
    /// se detiene y pregunta. «Seguir editando» conserva el formulario tal cual; «Salir y
    /// descartar» repite la navegación sin volver a preguntar.
    /// </summary>
    [Fact]
    public async Task Salir_con_el_formulario_a_medias_pregunta_y_deja_seguir_editando()
    {
        var cut = Renderizar(new MediatorVisitas());
        var navegacion = Services.GetRequiredService<NavigationManager>();
        var origen = navegacion.Uri;
        await AbrirNuevaVisitaAsync(cut);
        await cut.Find(".drawer-panel textarea").InputAsync(new ChangeEventArgs { Value = "Acceso por la puerta 3" });

        await cut.InvokeAsync(() => navegacion.NavigateTo("/documentos"));

        navegacion.Uri.Should().Be(origen, "con cambios sin guardar la navegación se detiene");
        cut.Find(".modal-contenido").TextContent.Should().Contain("¿Salir sin guardar?");

        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Seguir editando").ClickAsync(new MouseEventArgs());

        cut.FindAll(".modal-contenido").Should().BeEmpty();
        navegacion.Uri.Should().Be(origen);
        cut.FindComponents<CampoTextarea>().Should().Contain(c => c.Instance.Valor == "Acceso por la puerta 3", "el formulario sigue donde estaba");

        await cut.InvokeAsync(() => navegacion.NavigateTo("/documentos"));
        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Salir y descartar").ClickAsync(new MouseEventArgs());

        navegacion.Uri.Should().EndWith("/documentos", "confirmar la salida repite la navegación sin volver a preguntar");
        cut.FindAll(".drawer-panel").Should().BeEmpty(
            "si la navegación no desmonta la página (otros filtros en la URL), el drawer descartado no puede quedar abierto");
    }

    /// <summary>
    /// P1-E2, la otra mitad: abrir el formulario sin tocar nada, o cerrarlo con algo escrito,
    /// no deja nada que perder — salir no pregunta. Sin esto el aviso saltaría siempre y
    /// enseñaría a descartarlo sin leer.
    /// </summary>
    [Fact]
    public async Task Salir_sin_cambios_o_con_el_drawer_cerrado_no_pregunta()
    {
        var cut = Renderizar(new MediatorVisitas());
        var navegacion = Services.GetRequiredService<NavigationManager>();
        await AbrirNuevaVisitaAsync(cut);

        await cut.InvokeAsync(() => navegacion.NavigateTo("/documentos"));

        navegacion.Uri.Should().EndWith("/documentos", "abrir el formulario sin escribir nada no es un cambio");
        cut.FindAll(".modal-contenido").Should().BeEmpty();

        await AbrirNuevaVisitaAsync(cut);
        await cut.Find(".drawer-panel textarea").InputAsync(new ChangeEventArgs { Value = "Acceso por la puerta 3" });
        await cut.Find(".drawer-cerrar").ClickAsync(new MouseEventArgs());
        // P1-E2b (decisión del propietario, 2026-09-26): la X con cambios pregunta antes de cerrar.
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Descartar cambios").ClickAsync(new MouseEventArgs());

        await cut.InvokeAsync(() => navegacion.NavigateTo("/visitas"));

        navegacion.Uri.Should().EndWith("/visitas", "con el drawer cerrado ya no hay formulario que perder");
        cut.FindAll(".modal-contenido").Should().BeEmpty();
    }

    /// <summary>
    /// D-20: guardar sin trabajador dejaba el drawer abierto sin mensaje visible (la excepción de
    /// validación se guardaba en un diccionario que nadie pintaba). Ahora no se envía el comando,
    /// el error sale en línea junto a la lista y como aviso, y desaparece al marcar a alguien.
    /// </summary>
    [Fact]
    public async Task Guardar_sin_trabajador_avisa_en_linea_y_con_un_aviso_y_no_envia_el_comando()
    {
        var trabajador = Guid.NewGuid();
        var mediator = new MediatorVisitas
        {
            TrabajadoresSelector = [TrabajadorSelectorFalso.Crear(trabajador, "Iker Zubiri Olano")],
        };
        var cut = Renderizar(mediator);
        await cut.FindAll(".acciones-cabecera button").First(b => b.TextContent.Contains("Nueva visita")).ClickAsync(new MouseEventArgs());
        cut.FindAll(".campo-mensaje-error").Should().BeEmpty("barrera: antes de guardar no hay error");

        await cut.FindAll(".drawer-panel button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new MouseEventArgs());

        cut.Find(".drawer-panel .campo-mensaje-error").TextContent.Should().Contain("Marca al menos un trabajador");
        Services.GetRequiredService<ToastService>().Mensajes.Should().Contain(m => m.Mensaje.Contains("Marca al menos un trabajador"));
        mediator.Comandos.Should().BeEmpty();

        await cut.Find(".drawer-panel .lista-seleccion-multiple input").ChangeAsync(new ChangeEventArgs { Value = true });
        cut.FindAll(".drawer-panel .campo-mensaje-error").Should().BeEmpty("el error se quita al marcar a alguien");
    }

    /// <summary>D-21: el interruptor solo marca; el texto del formulario y el título del interruptor lo dicen.</summary>
    [Fact]
    public void El_interruptor_de_notificada_dice_que_solo_marca_y_no_envia_nada()
    {
        var mediator = new MediatorVisitas();
        mediator.Visitas.Add(Visita("Centro Norte"));
        var cut = Renderizar(mediator);

        Interruptor(cut, "Centro Norte").GetAttribute("title").Should().Contain("no envía ningún aviso");
    }

    /// <summary>D-21: una Visita dada de alta a mano figura con Origen «Manual», no «Plataforma»; las demás conservan el suyo.</summary>
    [Theory]
    [InlineData(OrigenVisita.Manual, "Manual")]
    [InlineData(OrigenVisita.Plataforma, "Plataforma")]
    [InlineData(OrigenVisita.Correo, "Correo")]
    [InlineData(OrigenVisita.WhatsApp, "WhatsApp")]
    public void El_origen_se_pinta_con_su_rotulo(OrigenVisita origen, string rotulo)
    {
        var mediator = new MediatorVisitas();
        mediator.Visitas.Add(Visita("Centro Norte", origen: origen));
        var cut = Renderizar(mediator);

        var rotulos = new[] { "Manual", "Plataforma", "Correo", "WhatsApp" };
        Fila(cut, "Centro Norte").QuerySelectorAll(".badge").Select(b => b.TextContent.Trim()).Where(rotulos.Contains)
            .Should().ContainSingle("la fila pinta un solo rótulo de Origen").Which.Should().Be(rotulo);
    }

    // ── D-20, segunda parte: «relacionado» = Trabajador asignado al Centro de la Visita ──

    private static readonly Guid CentroConAsignados = Guid.NewGuid();
    private static readonly Guid CentroSinAsignados = Guid.NewGuid();
    private static readonly Guid CentroOtro = Guid.NewGuid();
    private static readonly Guid Asignado = Guid.NewGuid();
    private static readonly Guid OtroAsignado = Guid.NewGuid();
    private static readonly Guid NoAsignado = Guid.NewGuid();

    private static MediatorVisitas MediatorConCentros() => new()
    {
        CentrosSelector =
        [
            new CentroSelectorDto(CentroConAsignados, "Planta Norte", "Cliente A", "Empresa A"),
            new CentroSelectorDto(CentroSinAsignados, "Planta Vacía", "Cliente A", "Empresa A"),
            new CentroSelectorDto(CentroOtro, "Planta Otra", "Cliente A", "Empresa A"),
        ],
        TrabajadoresSelector =
        [
            TrabajadorSelectorFalso.Crear(Asignado, "Iker Zubiri Olano"),
            TrabajadorSelectorFalso.Crear(OtroAsignado, "Maite Goikoetxea Arana"),
            TrabajadorSelectorFalso.Crear(NoAsignado, "Unai Etxeberria Ruiz"),
        ],
        AsignadosPorCentro =
        {
            [CentroConAsignados] = [Asignado],
            [CentroOtro] = [OtroAsignado],
        },
    };

    private async Task<IRenderedComponent<Visitas>> AbrirNuevaVisitaAsync(MediatorVisitas mediator)
    {
        var cut = Renderizar(mediator);
        await cut.FindAll(".acciones-cabecera button").First(b => b.TextContent.Contains("Nueva visita")).ClickAsync(new MouseEventArgs());
        return cut;
    }

    private static Task ElegirCentroAsync(IRenderedComponent<Visitas> cut, Guid centro) =>
        cut.Find(".drawer-panel select").ChangeAsync(new ChangeEventArgs { Value = centro.ToString() });

    private static string[] TrabajadoresListados(IRenderedComponent<Visitas> cut) =>
        cut.FindAll(".drawer-panel .lista-seleccion-multiple label").Select(l => l.TextContent.Trim()).ToArray();

    private static IElement CasillaRelacionados(IRenderedComponent<Visitas> cut) =>
        cut.FindAll(".drawer-panel .selector-multiple-controles input[type=checkbox]").Single();

    [Fact]
    public async Task Sin_centro_elegido_la_lista_no_esta_filtrada()
    {
        var cut = await AbrirNuevaVisitaAsync(MediatorConCentros());

        TrabajadoresListados(cut).Should().HaveCount(3);
        CasillaRelacionados(cut).HasAttribute("checked").Should().BeFalse();
    }

    [Fact]
    public async Task Al_elegir_centro_la_lista_abre_filtrada_a_sus_asignados_y_solo_relacionados_marcado()
    {
        var cut = await AbrirNuevaVisitaAsync(MediatorConCentros());

        await ElegirCentroAsync(cut, CentroConAsignados);

        CasillaRelacionados(cut).HasAttribute("checked").Should().BeTrue();
        TrabajadoresListados(cut).Should().ContainSingle().Which.Should().Contain("Iker Zubiri");
        cut.FindAll(".drawer-panel .alerta-info").Should().BeEmpty();
    }

    [Fact]
    public async Task Al_cambiar_de_centro_el_filtro_se_recalcula()
    {
        var cut = await AbrirNuevaVisitaAsync(MediatorConCentros());
        await ElegirCentroAsync(cut, CentroConAsignados);

        await ElegirCentroAsync(cut, CentroOtro);

        TrabajadoresListados(cut).Should().ContainSingle().Which.Should().Contain("Maite Goikoetxea");
        CasillaRelacionados(cut).HasAttribute("checked").Should().BeTrue();
    }

    [Fact]
    public async Task Centro_sin_asignados_avisa_y_desmarca_el_filtro_para_no_dejar_la_lista_vacia()
    {
        var cut = await AbrirNuevaVisitaAsync(MediatorConCentros());

        await ElegirCentroAsync(cut, CentroSinAsignados);

        cut.Find(".drawer-panel .alerta-info").TextContent.Should().Contain("Este centro no tiene trabajadores asignados");
        CasillaRelacionados(cut).HasAttribute("checked").Should().BeFalse();
        TrabajadoresListados(cut).Should().HaveCount(3);
    }

    [Fact]
    public async Task Desmarcar_solo_relacionados_muestra_el_resto_de_trabajadores_y_volver_a_marcar_filtra()
    {
        var cut = await AbrirNuevaVisitaAsync(MediatorConCentros());
        await ElegirCentroAsync(cut, CentroConAsignados);

        await CasillaRelacionados(cut).ChangeAsync(new ChangeEventArgs { Value = false });
        TrabajadoresListados(cut).Should().HaveCount(3);

        await CasillaRelacionados(cut).ChangeAsync(new ChangeEventArgs { Value = true });
        TrabajadoresListados(cut).Should().ContainSingle();
    }

    [Fact]
    public async Task Un_asignado_que_el_selector_no_ofrece_no_amplia_la_lista()
    {
        var mediator = MediatorConCentros();
        mediator.AsignadosPorCentro[CentroConAsignados] = [Guid.NewGuid()];
        var cut = await AbrirNuevaVisitaAsync(mediator);

        await ElegirCentroAsync(cut, CentroConAsignados);

        TrabajadoresListados(cut).Should().HaveCount(3, "solo se ofrecen los trabajadores que ya ofrecía el selector");
        cut.FindAll(".drawer-panel .alerta-info").Should().BeEmpty("el Centro sí tiene asignados: un aviso de «sin asignados» sería falso");
        CasillaRelacionados(cut).HasAttribute("checked").Should().BeFalse();
    }

    [Fact]
    public async Task Volver_a_sin_centro_quita_el_filtro_y_el_aviso()
    {
        var cut = await AbrirNuevaVisitaAsync(MediatorConCentros());
        await ElegirCentroAsync(cut, CentroSinAsignados);
        cut.FindAll(".drawer-panel .alerta-info").Should().ContainSingle("barrera: el aviso estaba");

        await cut.Find(".drawer-panel select").ChangeAsync(new ChangeEventArgs { Value = "" });

        cut.FindAll(".drawer-panel .alerta-info").Should().BeEmpty();
        TrabajadoresListados(cut).Should().HaveCount(3);
        CasillaRelacionados(cut).HasAttribute("checked").Should().BeFalse();
    }

    [Fact]
    public async Task Si_falla_la_consulta_de_asignados_la_lista_sigue_utilizable_sin_filtro()
    {
        var mediator = MediatorConCentros();
        mediator.FallarAsignados = true;
        var cut = await AbrirNuevaVisitaAsync(mediator);

        await ElegirCentroAsync(cut, CentroConAsignados);

        TrabajadoresListados(cut).Should().HaveCount(3);
        CasillaRelacionados(cut).HasAttribute("checked").Should().BeFalse();
    }

    [Fact]
    public async Task La_respuesta_tardia_de_un_centro_anterior_no_pisa_el_filtro_del_centro_actual()
    {
        var mediator = MediatorConCentros();
        var retenido = new TaskCompletionSource();
        mediator.AsignadosRetenidos[CentroConAsignados] = retenido;
        var cut = await AbrirNuevaVisitaAsync(mediator);

        var primera = ElegirCentroAsync(cut, CentroConAsignados);
        await ElegirCentroAsync(cut, CentroOtro);
        retenido.SetResult();
        await primera;

        TrabajadoresListados(cut).Should().ContainSingle().Which.Should().Contain("Maite Goikoetxea",
            "la respuesta tardía del primer Centro no debe sustituir a la del segundo");
    }

    [Fact]
    public async Task Un_trabajador_marcado_no_relacionado_sigue_visible_con_el_filtro_activo()
    {
        var cut = await AbrirNuevaVisitaAsync(MediatorConCentros());
        await ElegirCentroAsync(cut, CentroConAsignados);
        await CasillaRelacionados(cut).ChangeAsync(new ChangeEventArgs { Value = false });
        var casillaAjena = cut.FindAll(".drawer-panel .lista-seleccion-multiple label")
            .Single(l => l.TextContent.Contains("Unai Etxeberria")).QuerySelector("input")!;
        await casillaAjena.ChangeAsync(new ChangeEventArgs { Value = true });

        await CasillaRelacionados(cut).ChangeAsync(new ChangeEventArgs { Value = true });

        TrabajadoresListados(cut).Should().HaveCount(2).And.Contain(t => t.Contains("Unai Etxeberria"));
    }

    [Fact]
    public async Task Guardar_sin_trabajador_lleva_el_foco_al_campo_con_error()
    {
        var cut = await AbrirNuevaVisitaAsync(MediatorConCentros());

        await cut.FindAll(".drawer-panel button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new MouseEventArgs());

        var foco = JSInterop.VerifyFocusAsyncInvoke();
        foco.Arguments[0].Should().BeOfType<ElementReference>().Which.Id.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// S12 (lote 2a): el alta usa el kit DrawerFormulario, que pregunta al «Cancelar» como la X (D-05); la salida por navegación la fijan las pruebas de aviso de la pantalla.
    /// Esta prueba fija que la pantalla le pasa su «hay cambios» y su estado: sin cambios cierra, con las notas escritas pregunta.
    /// </summary>
    [Fact]
    public async Task El_alta_pregunta_al_cancelar_con_datos_escritos_y_sin_cambios_cierra()
    {
        var cut = Renderizar(new MediatorVisitas());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("+ Nueva visita"));

        await cut.FindAll("button").First(b => b.TextContent.Trim() == "+ Nueva visita").ClickAsync(new());
        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel").Should().NotBeEmpty());
        await cut.ComprobarQueCancelarSinCambiosCierraAsync(".drawer-pie", ".drawer-panel");

        await cut.FindAll("button").First(b => b.TextContent.Trim() == "+ Nueva visita").ClickAsync(new());
        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel").Should().NotBeEmpty());
        await cut.Find(".drawer-cuerpo textarea").InputAsync(new ChangeEventArgs { Value = "Entrada por el muelle norte" });
        await cut.PulsarCancelarDelPieAsync(".drawer-pie");

        await cut.ComprobarQuePreguntaYDescartarAsync(".drawer-panel");
    }

    // ---------------------------------------------------------------- Diálogos con «Motivo (opcional)»: regla de Chris (2026-09-29, toda pérdida de edición pregunta)

    private const string TextoDelMotivo = "Obra aplazada por lluvia";
    private const string Cancelar = "cancelar";
    private const string Reactivar = "reactivar";
    private const string Lote = "lote";

    /// <summary>Abre uno de los tres diálogos de confirmación con motivo (cancelar, reactivar, cancelar en lote) y escribe el motivo, si hay.</summary>
    private async Task<(IRenderedComponent<Visitas> Cut, MediatorVisitas Mediator)> AbrirDialogoConMotivoAsync(string cual, string motivo)
    {
        this.ConRolDeEscritura(Roles.GestorCae);
        var mediator = new MediatorVisitas();
        var visita = Visita("Planta Zaragoza") with { EstaCancelada = cual == Reactivar };
        mediator.Visitas.Add(visita);
        var cut = Renderizar(mediator);

        switch (cual)
        {
            case Cancelar:
                await ItemDeMenu(cut, "Planta Zaragoza", "Cancelar visita").ClickAsync(new MouseEventArgs());
                break;
            case Reactivar:
                await cut.FindAll("input[type=checkbox]").First(c => c.ParentElement!.TextContent.Contains("Solo activas"))
                    .ChangeAsync(new ChangeEventArgs { Value = false });
                await ItemDeMenu(cut, "Planta Zaragoza", "Reactivar").ClickAsync(new MouseEventArgs());
                break;
            default:
                await cut.FindAll("button").First(b => b.TextContent.Trim() == "Selección múltiple").ClickAsync(new MouseEventArgs());
                await Fila(cut, "Planta Zaragoza").QuerySelector("input[type=checkbox]:not(.visitas-interruptor)")!
                    .ChangeAsync(new ChangeEventArgs { Value = true });
                await cut.FindAll(".barra-acciones-lote button").First(b => b.TextContent.Trim() == "Cancelar seleccionadas")
                    .ClickAsync(new MouseEventArgs());
                break;
        }

        cut.FindAll("[role=dialog] textarea").Should().ContainSingle("el diálogo de confirmación está abierto con su motivo");
        if (motivo.Length > 0)
            await cut.Find("[role=dialog] textarea").InputAsync(new ChangeEventArgs { Value = motivo });
        return (cut, mediator);
    }

    private static string BotonConfirmar(string cual) => cual switch
    {
        Cancelar => "Cancelar visita",
        Reactivar => "Reactivar",
        _ => "Cancelar seleccionadas",
    };

    private static string? MotivoEnviado(MediatorVisitas mediator, string cual) => cual switch
    {
        Cancelar => mediator.Comandos.OfType<CancelarVisitaCommand>().Single().Motivo,
        Reactivar => mediator.Comandos.OfType<ReactivarVisitaCommand>().Single().Motivo,
        _ => mediator.Comandos.OfType<CancelarVisitasCommand>().Single().Motivo,
    };

    /// <summary>Las cuatro salidas de un diálogo: «Volver», la X, Escape y el clic en el fondo; todas deben cerrar «como la X».</summary>
    private static Task PulsarSalidaAsync(IRenderedComponent<Visitas> cut, string salida) => salida switch
    {
        "X" => cut.Find(".modal-cerrar").ClickAsync(new MouseEventArgs()),
        "Escape" => cut.Find(".modal-contenido").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" }),
        "Fondo" => cut.Find(".modal-superposicion").ClickAsync(new MouseEventArgs()),
        _ => cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Volver").ClickAsync(new MouseEventArgs()),
    };

    private static bool PreguntaDescartar(IRenderedComponent<Visitas> cut) =>
        cut.FindAll("h2").Any(h => h.TextContent.Trim() == "¿Descartar cambios?");

    [Theory]
    [InlineData(Cancelar, "Volver")]
    [InlineData(Cancelar, "X")]
    [InlineData(Reactivar, "Volver")]
    [InlineData(Reactivar, "X")]
    [InlineData(Lote, "Volver")]
    [InlineData(Lote, "X")]
    [InlineData(Cancelar, "Escape")]
    [InlineData(Cancelar, "Fondo")]
    [InlineData(Reactivar, "Escape")]
    [InlineData(Reactivar, "Fondo")]
    [InlineData(Lote, "Escape")]
    [InlineData(Lote, "Fondo")]
    public async Task Cualquier_salida_con_motivo_escrito_pregunta_y_seguir_editando_lo_conserva(string cual, string salida)
    {
        var (cut, mediator) = await AbrirDialogoConMotivoAsync(cual, TextoDelMotivo);

        await PulsarSalidaAsync(cut, salida);

        PreguntaDescartar(cut).Should().BeTrue($"«{salida}» con el motivo escrito pregunta antes de tirarlo");
        await cut.PulsarEnElAvisoAsync("Seguir editando");

        PreguntaDescartar(cut).Should().BeFalse();
        cut.FindComponents<CampoTextarea>().Should().Contain(c => c.Instance.Valor == TextoDelMotivo, "«Seguir editando» conserva lo escrito");
        mediator.Comandos.Should().BeEmpty("salir no confirma nada");

        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == BotonConfirmar(cual)).ClickAsync(new MouseEventArgs());
        MotivoEnviado(mediator, cual).Should().Be(TextoDelMotivo, "el motivo que viaja en el comando es el que no se descartó");
    }

    [Theory]
    [InlineData(Cancelar, "Volver")]
    [InlineData(Cancelar, "X")]
    [InlineData(Reactivar, "Volver")]
    [InlineData(Reactivar, "X")]
    [InlineData(Lote, "Volver")]
    [InlineData(Lote, "X")]
    [InlineData(Cancelar, "Escape")]
    [InlineData(Cancelar, "Fondo")]
    [InlineData(Reactivar, "Escape")]
    [InlineData(Reactivar, "Fondo")]
    [InlineData(Lote, "Escape")]
    [InlineData(Lote, "Fondo")]
    public async Task Descartar_cambios_cierra_el_dialogo_sin_enviar_nada(string cual, string salida)
    {
        var (cut, mediator) = await AbrirDialogoConMotivoAsync(cual, TextoDelMotivo);

        await PulsarSalidaAsync(cut, salida);
        await cut.PulsarEnElAvisoAsync("Descartar cambios");

        cut.FindAll("[role=dialog]").Should().BeEmpty("al descartar se cierra");
        mediator.Comandos.Should().BeEmpty("volver no cancela ni reactiva nada");
    }

    [Theory]
    [InlineData(Cancelar, "Volver", "")]
    [InlineData(Cancelar, "X", "")]
    [InlineData(Cancelar, "Volver", "   ")]
    [InlineData(Reactivar, "Volver", "")]
    [InlineData(Reactivar, "X", "")]
    [InlineData(Reactivar, "X", "   ")]
    [InlineData(Lote, "Volver", "")]
    [InlineData(Lote, "X", "")]
    [InlineData(Lote, "Volver", "   ")]
    [InlineData(Cancelar, "Escape", "")]
    [InlineData(Reactivar, "Fondo", "")]
    [InlineData(Lote, "Escape", "   ")]
    public async Task Cualquier_salida_sin_motivo_escrito_cierran_sin_preguntar(string cual, string salida, string motivo)
    {
        var (cut, mediator) = await AbrirDialogoConMotivoAsync(cual, motivo);

        await PulsarSalidaAsync(cut, salida);

        PreguntaDescartar(cut).Should().BeFalse("sin nada escrito no hay nada que perder");
        cut.FindAll("[role=dialog]").Should().BeEmpty();
        mediator.Comandos.Should().BeEmpty();
    }

    /// <summary>Salir de la pantalla con el motivo a medias pregunta; sin motivo, o con el diálogo ya cerrado, no.</summary>
    [Theory]
    [InlineData(Cancelar)]
    [InlineData(Reactivar)]
    [InlineData(Lote)]
    public async Task Salir_de_la_pantalla_con_el_motivo_a_medias_pregunta_y_sin_el_no(string cual)
    {
        var (cut, _) = await AbrirDialogoConMotivoAsync(cual, TextoDelMotivo);
        var navegacion = Services.GetRequiredService<NavigationManager>();
        var origen = navegacion.Uri;

        await cut.SalirYComprobarQuePreguntaAsync(navegacion);
        await cut.PulsarEnElAvisoAsync("Seguir editando");
        navegacion.Uri.Should().Be(origen);
        cut.FindComponents<CampoTextarea>().Should().Contain(c => c.Instance.Valor == TextoDelMotivo, "«Seguir editando» no toca lo escrito");

        await cut.SalirYComprobarQuePreguntaAsync(navegacion);
        await cut.PulsarEnElAvisoAsync("Salir y descartar");
        navegacion.Uri.Should().EndWith(AvisoCambiosSinGuardarPrueba.DestinoFuera);
        cut.FindAll("[role=dialog]").Should().BeEmpty("«Salir y descartar» cierra el diálogo de motivo (AlDescartar)");
    }

    [Theory]
    [InlineData(Cancelar)]
    [InlineData(Reactivar)]
    [InlineData(Lote)]
    public async Task Salir_de_la_pantalla_con_el_dialogo_sin_motivo_no_pregunta(string cual)
    {
        var (cut, _) = await AbrirDialogoConMotivoAsync(cual, string.Empty);

        await cut.SalirYComprobarQueNoPreguntaAsync(Services.GetRequiredService<NavigationManager>(), "sin motivo no hay nada que perder");
    }

    /// <summary>
    /// Tras confirmar, el diálogo se cierra y el texto que queda en el campo (el motivo no se limpia hasta volver a abrirlo) ya no es
    /// «cambios sin guardar»: salir no pregunta.
    /// </summary>
    [Theory]
    [InlineData(Cancelar)]
    [InlineData(Reactivar)]
    [InlineData(Lote)]
    public async Task Tras_confirmar_con_motivo_salir_de_la_pantalla_no_pregunta(string cual)
    {
        var (cut, mediator) = await AbrirDialogoConMotivoAsync(cual, TextoDelMotivo);

        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == BotonConfirmar(cual)).ClickAsync(new MouseEventArgs());

        MotivoEnviado(mediator, cual).Should().Be(TextoDelMotivo);
        cut.WaitForAssertion(() => cut.FindAll("[role=dialog]").Should().BeEmpty());
        await cut.SalirYComprobarQueNoPreguntaAsync(Services.GetRequiredService<NavigationManager>(), "el diálogo ya se cerró al confirmar");
    }
}
