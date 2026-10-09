using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.EnviarMensajeNuevo;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;
using CaeManager.Application.Visitas.Commands.MarcarDocumentacionGestionada;
using CaeManager.Application.Visitas.Queries.ObtenerDetalleVisita;
using CaeManager.Application.Visitas.Queries.ObtenerDocumentacionVisita;
using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Visitas.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// «Documentación gestionada» en /visitas (decisión del propietario, 2026-10-09): la columna
/// pinta el estado GUARDADO en la Visita —no si los documentos están vigentes— y el panel
/// ofrece la salida manual. La regla (quién puede, qué borra la marca) vive en Application y
/// se prueba allí; aquí solo que la pantalla la pinta y manda el comando con Id y versión.
/// </summary>
public class VisitasDocumentacionGestionadaTests : BunitContext
{
    private static readonly DateOnly Hoy = DiaDeNegocio.Hoy();

    public VisitasDocumentacionGestionadaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        this.ConRolDeEscritura();
    }

    private sealed class MediatorPanel : IMediator
    {
        public Guid VisitaId { get; } = Guid.NewGuid();
        public Guid Version { get; private set; } = Guid.NewGuid();
        public DateTime? GestionadaEn { get; set; }
        public bool DocumentosVigentes { get; set; } = true;
        public bool RequiereGestionCae { get; set; } = true;
        public Result RespuestaAlMarcar { get; set; } = Result.Exito();
        public List<MarcarDocumentacionGestionadaCommand> Marcas { get; } = [];

        private VisitaListaDto Fila() => new(
            VisitaId, Guid.NewGuid(), "Centro Norte", Guid.NewGuid(), "Iberojet S.A.", Guid.NewGuid(), "Instalaciones Arbeko S.L.",
            Hoy, Hoy.AddDays(2), TotalTrabajadores: 1, DocumentacionCompleta: DocumentosVigentes, NotificadoCliente: false,
            OrigenVisita.Correo, NivelUrgenciaVisita.Urgente, CentroRequiereGestionCae: RequiereGestionCae, Version: Version,
            Trabajadores: ["Ana García Ruiz"], DocumentacionGestionadaEnUtc: GestionadaEn);

        private DetalleVisitaDto Detalle() => new(
            VisitaId, "Centro Norte", "Iberojet S.A.", Guid.NewGuid(), "Instalaciones Arbeko S.L.", Hoy, Hoy.AddDays(2),
            Notas: null, NotificadoCliente: false, Trabajadores: [new TrabajadorVisitaDto(Guid.NewGuid(), "Ana García Ruiz")],
            HoraEstimadaAcceso: null, FechaHoraSolicitudUtc: null, FechaHoraExpedienteCompletoUtc: null,
            AntelacionNominalHoras: null, AntelacionEfectivaHoras: null, Tramo: null, AtribucionUrgencia.SinUrgencia,
            CentroRequiereGestionCae: RequiereGestionCae, Version: Version, DocumentacionGestionadaEnUtc: GestionadaEn);

        private static Task<T> Respuesta<T>(object? valor) => Task.FromResult((T)valor!);

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            switch (request)
            {
                case ObtenerVisitasQuery consulta:
                    return Respuesta<TResponse>(new ResultadoPaginado<VisitaListaDto>([Fila()], 1, consulta.Pagina, consulta.TamanoPagina));

                case ObtenerDetalleVisitaQuery:
                    return Respuesta<TResponse>(Detalle());

                case ObtenerDocumentacionVisitaQuery:
                    return Respuesta<TResponse>(new DocumentacionVisitaDto(Guid.NewGuid(), new SeccionDocumentacionDto(EstadoDocumento.Vigente, []), []));

                case MarcarDocumentacionGestionadaCommand marcar:
                    Marcas.Add(marcar);
                    if (RespuestaAlMarcar.EsExitoso)
                    {
                        GestionadaEn = new DateTime(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc);
                        Version = Guid.NewGuid();
                    }
                    return Respuesta<TResponse>(RespuestaAlMarcar);

                default:
                    throw new NotSupportedException(request.GetType().Name);
            }
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    private const string BotonMarcar = "Marcar documentación gestionada";

    private IRenderedComponent<Visitas> Renderizar(MediatorPanel mediator)
    {
        Services.AddScoped<IMediator>(_ => mediator);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        var cut = Render<Visitas>();
        cut.WaitForAssertion(() => cut.Find("tr .menu-acciones-disparador"));
        return cut;
    }

    private async Task<IRenderedComponent<Visitas>> AbrirPanelAsync(MediatorPanel mediator)
    {
        var cut = Renderizar(mediator);
        await cut.Find("tr .menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".menu-acciones-item").First(i => i.TextContent.Trim() == "Ver").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel [role=tab]").Should().NotBeEmpty());
        return cut;
    }

    private static IElement Fila(IRenderedComponent<Visitas> cut) =>
        cut.FindAll("tbody tr").First(tr => tr.TextContent.Contains("Centro Norte"));

    private static IReadOnlyList<IElement> BotonesMarcar(IRenderedComponent<Visitas> cut) =>
        cut.FindAll(".drawer-panel button").Where(b => b.TextContent.Trim() == BotonMarcar).ToList();

    /// <summary>
    /// El caso que decide la regla: con todos los documentos vigentes la fila sigue en rojo
    /// «Por gestionar». Antes pintaba «Completa» en verde.
    /// </summary>
    [Fact]
    public void Con_los_documentos_vigentes_y_sin_marca_la_fila_dice_Por_gestionar()
    {
        var cut = Renderizar(new MediatorPanel { DocumentosVigentes = true, GestionadaEn = null });

        var fila = Fila(cut);
        fila.QuerySelectorAll(".badge-peligro").Should().ContainSingle().Which.TextContent.Trim().Should().Be("Por gestionar");
        fila.TextContent.Should().NotContain("Gestionada").And.NotContain("Completa");
        fila.QuerySelector("span[title^='Documentos vigentes']").Should().NotBeNull("el title dice que ya se puede enviar");
    }

    [Fact]
    public void Sin_marca_y_con_documentos_que_faltan_el_title_lo_dice()
    {
        var cut = Renderizar(new MediatorPanel { DocumentosVigentes = false });

        Fila(cut).QuerySelector("span[title='Faltan documentos vigentes']").Should().NotBeNull();
    }

    /// <summary>Marcada, la fila es verde aunque después falte un documento: es un estado guardado.</summary>
    [Fact]
    public void Con_la_marca_la_fila_dice_Gestionada_aunque_falten_documentos()
    {
        var cut = Renderizar(new MediatorPanel
        {
            DocumentosVigentes = false,
            GestionadaEn = new DateTime(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc)
        });

        var fila = Fila(cut);
        fila.QuerySelectorAll(".badge-exito").Should().ContainSingle().Which.TextContent.Trim().Should().Be("Gestionada");
        fila.TextContent.Should().NotContain("Por gestionar");
        fila.QuerySelector("span[title^='Documentación gestionada el']").Should().NotBeNull();
    }

    [Fact]
    public async Task El_panel_de_una_visita_por_gestionar_ofrece_marcarla_y_manda_el_comando_con_su_version()
    {
        var mediator = new MediatorPanel();
        var versionVista = mediator.Version;
        var cut = await AbrirPanelAsync(mediator);

        cut.WaitForAssertion(() => BotonesMarcar(cut).Should().ContainSingle());
        await BotonesMarcar(cut).Single().ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => mediator.Marcas.Should().ContainSingle());
        mediator.Marcas.Single().Should().Be(new MarcarDocumentacionGestionadaCommand(mediator.VisitaId, versionVista),
            "viaja la versión que se vio: si alguien cambió los Trabajadores, el servidor lo rechaza");
        cut.WaitForAssertion(() =>
        {
            Fila(cut).QuerySelectorAll(".badge-exito").Should().ContainSingle().Which.TextContent.Trim().Should().Be("Gestionada");
            BotonesMarcar(cut).Should().BeEmpty("ya gestionada, no se ofrece marcarla otra vez");
            cut.Find(".drawer-panel").TextContent.Should().Contain("Gestionada el");
        });
    }

    [Fact]
    public async Task Si_el_servidor_rechaza_la_marca_la_visita_sigue_por_gestionar_y_se_avisa()
    {
        var mediator = new MediatorPanel
        {
            RespuestaAlMarcar = Result.Fallo(Error.Crear(ConcurrenciaOptimista.CodigoConflicto, "Otra persona modificó esta visita mientras lo editabas."))
        };
        var cut = await AbrirPanelAsync(mediator);

        cut.WaitForAssertion(() => BotonesMarcar(cut).Should().ContainSingle());
        await BotonesMarcar(cut).Single().ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => mediator.Marcas.Should().ContainSingle());
        cut.WaitForAssertion(() =>
        {
            Fila(cut).QuerySelectorAll(".badge-peligro").Should().ContainSingle().Which.TextContent.Trim().Should().Be("Por gestionar");
            Services.GetRequiredService<ToastService>().Mensajes.Should().Contain(m => m.Mensaje.Contains("Otra persona modificó"));
        });
    }

    [Fact]
    public async Task Consulta_ve_el_estado_pero_no_se_le_ofrece_marcar()
    {
        // MarcarDocumentacionGestionadaCommand es un ICommand que AutorizacionEscrituraBehavior deniega a Consulta.
        this.ConRolDeEscritura(Roles.Consulta);
        var cut = await AbrirPanelAsync(new MediatorPanel());

        cut.WaitForAssertion(() => cut.Find(".drawer-panel").TextContent.Should().Contain("Por gestionar"));
        BotonesMarcar(cut).Should().BeEmpty();
    }

    [Fact]
    public async Task En_un_Centro_sin_gestion_CAE_no_hay_nada_que_marcar()
    {
        var cut = await AbrirPanelAsync(new MediatorPanel { RequiereGestionCae = false });

        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel [role=tab]").Should().NotBeEmpty());
        BotonesMarcar(cut).Should().BeEmpty();
        Fila(cut).TextContent.Should().NotContain("Por gestionar").And.NotContain("Gestionada");
    }
}

/// <summary>
/// El compositor compartido envía por el comando que le pasa quien lo abre (<c>EnviarCon</c>):
/// así el envío del paquete de acreditación pasa por el comando de Visitas, que marca la
/// documentación como gestionada, en vez de por el envío genérico de Comunicaciones.
/// </summary>
public class RedactarMensajeDrawerEnviarConTests : BunitContext
{
    private readonly MediatorDeBuzones _mediator = new();

    public RedactarMensajeDrawerEnviarConTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddScoped<IMediator>(_ => _mediator);
        Services.AddScoped<ToastService>();
        this.ConRolDeEscritura();
    }

    private sealed class MediatorDeBuzones : IMediator
    {
        public List<EnviarMensajeNuevoCommand> EnviosGenericos { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            switch (request)
            {
                case ObtenerConexionesIntegracionQuery:
                    IReadOnlyList<ConexionIntegracionListaDto> conexiones =
                        [new(Guid.NewGuid(), "cae@example.invalid", "CAE Norte", null, null, EstadoConexionIntegracion.Habilitada, DateTime.UtcNow, null, null)];
                    return Task.FromResult((TResponse)(object)conexiones);
                case EnviarMensajeNuevoCommand envio:
                    EnviosGenericos.Add(envio);
                    return Task.FromResult((TResponse)(object)Result.Exito(Guid.NewGuid()));
                default:
                    throw new NotSupportedException(request.GetType().Name);
            }
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    private IRenderedComponent<RedactarMensajeDrawer> Abrir(
        Func<EnviarMensajeNuevoCommand, Task<Result<Guid>>>? enviarCon, List<string> enviados)
    {
        var cut = Render<RedactarMensajeDrawer>(p =>
        {
            p.Add(x => x.Visible, true)
                .Add(x => x.DestinatariosIniciales, "acceso@centronorte.es")
                .Add(x => x.AsuntoInicial, "Solicitud de acceso")
                .Add(x => x.CuerpoInicial, "Adjuntamos la documentación vigente.")
                .Add(x => x.Adjunto, new AdjuntoParaEnviarDto("paquete.zip", "application/zip", [1, 2, 3]))
                .Add(x => x.OnEnviado, () => enviados.Add("enviado"));
            if (enviarCon is not null)
                p.Add(x => x.EnviarCon, enviarCon);
        });
        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel button").Should().Contain(b => b.TextContent.Trim() == "Enviar"));
        return cut;
    }

    private static IElement BotonEnviar(IRenderedComponent<RedactarMensajeDrawer> cut) =>
        cut.FindAll(".drawer-panel button").Single(b => b.TextContent.Trim() == "Enviar");

    [Fact]
    public async Task Con_EnviarCon_el_mensaje_compuesto_sale_por_ese_comando_y_no_por_el_envio_generico()
    {
        var recibidos = new List<EnviarMensajeNuevoCommand>();
        var enviados = new List<string>();
        var cut = Abrir(m => { recibidos.Add(m); return Task.FromResult(Result.Exito(Guid.NewGuid())); }, enviados);

        await BotonEnviar(cut).ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => recibidos.Should().ContainSingle());
        var mensaje = recibidos.Single();
        mensaje.Destinatarios.Should().Equal("acceso@centronorte.es");
        mensaje.Asunto.Should().Be("Solicitud de acceso");
        mensaje.Adjuntos.Should().ContainSingle().Which.NombreArchivo.Should().Be("paquete.zip");
        _mediator.EnviosGenericos.Should().BeEmpty("quien abre el compositor decide por qué comando sale");
        enviados.Should().ContainSingle();
    }

    [Fact]
    public async Task Si_el_comando_de_EnviarCon_falla_el_compositor_muestra_el_error_y_no_da_el_envio_por_hecho()
    {
        var enviados = new List<string>();
        var cut = Abrir(
            _ => Task.FromResult(Result.Fallo<Guid>(Error.Crear(ConcurrenciaOptimista.CodigoConflicto, "Otra persona modificó esta visita mientras lo editabas."))),
            enviados);

        await BotonEnviar(cut).ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Find(".drawer-panel").TextContent.Should().Contain("Otra persona modificó esta visita"));
        enviados.Should().BeEmpty();
        _mediator.EnviosGenericos.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_EnviarCon_el_compositor_sigue_enviando_con_el_envio_generico()
    {
        var enviados = new List<string>();
        var cut = Abrir(enviarCon: null, enviados);

        await BotonEnviar(cut).ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => _mediator.EnviosGenericos.Should().ContainSingle());
        enviados.Should().ContainSingle();
    }
}
