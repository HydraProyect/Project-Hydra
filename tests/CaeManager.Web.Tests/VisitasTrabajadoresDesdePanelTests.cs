using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Application.Visitas.Commands.AnadirTrabajadorAVisita;
using CaeManager.Application.Visitas.Commands.QuitarTrabajadorDeVisita;
using CaeManager.Application.Visitas.Queries.ObtenerCandidatosTrabajadorVisita;
using CaeManager.Application.Visitas.Queries.ObtenerDetalleVisita;
using CaeManager.Application.Visitas.Queries.ObtenerDocumentacionVisita;
using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.Identity;
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
/// Pestaña «Trabajadores» del panel de una Visita: quién entra, baja con el icono «menos» y alta
/// desde una lista con buscador. El doble del mediador <b>aplica</b> los comandos a su estado y la
/// pantalla se vuelve a leer de él: si la página no enviara el comando, o no recargara, se vería.
/// </summary>
public class VisitasTrabajadoresDesdePanelTests : BunitContext
{
    private static readonly DateOnly Hoy = DiaDeNegocio.Hoy();

    public VisitasTrabajadoresDesdePanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        this.ConRolDeEscritura();
    }

    private sealed class MediatorPanel : IMediator
    {
        public Guid VisitaId { get; } = Guid.NewGuid();
        public Guid Version { get; private set; } = Guid.NewGuid();
        public bool Cancelada { get; set; }
        public List<TrabajadorVisitaDto> Entran { get; } = [];
        public List<TrabajadorSelectorDto> Candidatos { get; } = [];
        public List<object> Comandos { get; } = [];
        public int ConsultasCandidatos { get; private set; }

        private VisitaListaDto Fila() => new(
            VisitaId, Guid.NewGuid(), "Centro Norte", Guid.NewGuid(), "Iberojet S.A.", Guid.NewGuid(), "Instalaciones Arbeko S.L.",
            Hoy, Hoy.AddDays(2), TotalTrabajadores: Entran.Count, DocumentacionCompleta: false, NotificadoCliente: false,
            OrigenVisita.Correo, NivelUrgenciaVisita.Urgente, EstaCancelada: Cancelada, Version: Version,
            Trabajadores: Entran.Select(t => t.NombreCompleto).ToList());

        private DetalleVisitaDto Detalle() => new(
            VisitaId, "Centro Norte", "Iberojet S.A.", Guid.NewGuid(), "Instalaciones Arbeko S.L.", Hoy, Hoy.AddDays(2),
            Notas: null, NotificadoCliente: false, Trabajadores: Entran.ToList(), HoraEstimadaAcceso: null, FechaHoraSolicitudUtc: null,
            FechaHoraExpedienteCompletoUtc: null, AntelacionNominalHoras: null, AntelacionEfectivaHoras: null, Tramo: null,
            AtribucionUrgencia.SinUrgencia, EstaCancelada: Cancelada, Version: Version);

        private static Task<T> Respuesta<T>(object? valor) => Task.FromResult((T)valor!);

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            switch (request)
            {
                case ObtenerVisitasQuery consulta:
                    var filas = consulta.SoloActivas && Cancelada ? new List<VisitaListaDto>() : [Fila()];
                    return Respuesta<TResponse>(new ResultadoPaginado<VisitaListaDto>(filas, filas.Count, consulta.Pagina, consulta.TamanoPagina));

                case ObtenerDetalleVisitaQuery:
                    return Respuesta<TResponse>(Detalle());

                case ObtenerDocumentacionVisitaQuery:
                    return Respuesta<TResponse>(new DocumentacionVisitaDto(Guid.NewGuid(), new SeccionDocumentacionDto(EstadoDocumento.Vigente, []), []));

                case ObtenerCandidatosTrabajadorVisitaQuery:
                    ConsultasCandidatos++;
                    return Respuesta<TResponse>((IReadOnlyList<TrabajadorSelectorDto>)Candidatos.ToList());

                case AnadirTrabajadorAVisitaCommand anadir:
                    {
                        Comandos.Add(anadir);
                        var candidato = Candidatos.Single(c => c.Id == anadir.TrabajadorId);
                        Candidatos.Remove(candidato);
                        Entran.Add(new TrabajadorVisitaDto(candidato.Id, candidato.NombreCompleto));
                        Version = Guid.NewGuid();
                        return Respuesta<TResponse>(Result.Exito());
                    }

                case QuitarTrabajadorDeVisitaCommand quitar:
                    Comandos.Add(quitar);
                    Entran.RemoveAll(t => t.Id == quitar.TrabajadorId);
                    Version = Guid.NewGuid();
                    return Respuesta<TResponse>(Result.Exito());

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

    private static MediatorPanel ConTrabajadores(params string[] nombres)
    {
        var mediator = new MediatorPanel();
        foreach (var nombre in nombres)
            mediator.Entran.Add(new TrabajadorVisitaDto(Guid.NewGuid(), nombre));
        return mediator;
    }

    private static TrabajadorSelectorDto Candidato(string nombre) => new(Guid.NewGuid(), nombre, Alias: null, EmpleadorNombre: "Instalaciones Arbeko S.L.");

    /// <summary>Monta la página, abre el panel de la única visita y entra en la pestaña «Trabajadores».</summary>
    private async Task<IRenderedComponent<Visitas>> AbrirPestanaTrabajadoresAsync(MediatorPanel mediator)
    {
        Services.AddScoped<IMediator>(_ => mediator);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        var cut = Render<Visitas>();

        if (mediator.Cancelada)
        {
            await cut.FindAll("input[type=checkbox]").First(c => c.ParentElement!.TextContent.Contains("Solo activas"))
                .ChangeAsync(new ChangeEventArgs { Value = false });
        }

        cut.WaitForAssertion(() => cut.Find("tr .menu-acciones-disparador"));
        await cut.Find("tr .menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".menu-acciones-item").First(i => i.TextContent.Trim() == "Ver").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel [role=tab]").Should().HaveCount(3));
        await cut.FindAll(".drawer-panel [role=tab]").First(t => t.TextContent.Trim() == "Trabajadores").ClickAsync(new MouseEventArgs());
        return cut;
    }

    private static IReadOnlyList<string> NombresQueEntran(IRenderedComponent<Visitas> cut) =>
        cut.FindAll(".visitas-trabajador-nombre").Select(n => n.TextContent.Trim()).ToList();

    private static IReadOnlyList<IElement> BotonesQuitar(IRenderedComponent<Visitas> cut) => cut.FindAll(".visitas-trabajador-quitar");

    private static IElement BotonConTexto(IRenderedComponent<Visitas> cut, string texto) =>
        cut.FindAll("button").First(b => b.TextContent.Trim() == texto);

    private static string PestanaActiva(IRenderedComponent<Visitas> cut) =>
        cut.Find(".drawer-panel [role=tab][aria-selected=true]").TextContent.Trim();

    [Fact]
    public async Task El_panel_se_abre_en_Informacion_y_ofrece_las_tres_pestanas()
    {
        var mediator = ConTrabajadores("Ana García Ruiz");
        Services.AddScoped<IMediator>(_ => mediator);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        var cut = Render<Visitas>();
        cut.WaitForAssertion(() => cut.Find("tr .menu-acciones-disparador"));
        await cut.Find("tr .menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".menu-acciones-item").First(i => i.TextContent.Trim() == "Ver").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel [role=tab]").Select(t => t.TextContent.Trim())
            .Should().Equal(["Información", "Trabajadores", "Documentación"]));
        PestanaActiva(cut).Should().Be("Información");
        cut.FindAll(".visitas-trabajador-fila").Should().BeEmpty("la lista de quién entra vive en su pestaña");
    }

    [Fact]
    public async Task La_pestana_lista_a_quien_entra_con_un_boton_menos_de_nombre_accesible_propio()
    {
        var cut = await AbrirPestanaTrabajadoresAsync(ConTrabajadores("Ana García Ruiz", "Óscar Bravo Nieto"));

        NombresQueEntran(cut).Should().Equal(["Ana García Ruiz", "Óscar Bravo Nieto"]);
        BotonesQuitar(cut).Select(b => b.GetAttribute("aria-label")).Should().Equal(
            ["Quitar a Ana García Ruiz de la visita", "Quitar a Óscar Bravo Nieto de la visita"]);
        BotonesQuitar(cut).Should().OnlyContain(b => !b.HasAttribute("disabled"));
        BotonesQuitar(cut).Should().OnlyContain(b => b.TextContent.Trim().Length == 0 && b.QuerySelector("svg") != null,
            "se quita con el icono «menos», no con la palabra «Quitar»");
    }

    [Fact]
    public async Task Quitar_pregunta_antes_y_al_confirmar_manda_el_comando_y_sigue_en_la_pestana()
    {
        var mediator = ConTrabajadores("Ana García Ruiz", "Óscar Bravo Nieto");
        var versionAlAbrir = mediator.Version;
        var oscar = mediator.Entran[1];
        var cut = await AbrirPestanaTrabajadoresAsync(mediator);

        await BotonesQuitar(cut)[1].ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("¿Quitar a Óscar Bravo Nieto de la visita?"));
        mediator.Comandos.Should().BeEmpty("toda pérdida de edición pregunta: el clic en «menos» no quita por sí solo");

        await BotonConTexto(cut, "Quitar de la visita").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => NombresQueEntran(cut).Should().Equal(["Ana García Ruiz"]));
        mediator.Comandos.Should().ContainSingle().Which.Should().Be(
            new QuitarTrabajadorDeVisitaCommand(mediator.VisitaId, oscar.Id, versionAlAbrir));
        PestanaActiva(cut).Should().Be("Trabajadores", "tras quitar se sigue donde se estaba, no se vuelve a «Información»");
    }

    [Fact]
    public async Task Volver_en_la_confirmacion_no_quita_a_nadie()
    {
        var mediator = ConTrabajadores("Ana García Ruiz", "Óscar Bravo Nieto");
        var cut = await AbrirPestanaTrabajadoresAsync(mediator);

        await BotonesQuitar(cut)[0].ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("¿Quitar a Ana García Ruiz de la visita?"));
        await BotonConTexto(cut, "Volver").ClickAsync(new MouseEventArgs());

        mediator.Comandos.Should().BeEmpty();
        NombresQueEntran(cut).Should().HaveCount(2);
    }

    [Fact]
    public async Task Al_unico_trabajador_no_se_le_puede_quitar()
    {
        var cut = await AbrirPestanaTrabajadoresAsync(ConTrabajadores("Ana García Ruiz"));

        var boton = BotonesQuitar(cut).Single();
        boton.HasAttribute("disabled").Should().BeTrue();
        boton.GetAttribute("title").Should().Be("La visita debe incluir al menos un trabajador");
    }

    [Fact]
    public async Task Con_un_unico_candidato_Anadir_lo_anade_directamente_sin_abrir_la_lista()
    {
        var mediator = ConTrabajadores("Ana García Ruiz");
        var unico = Candidato("Noelia Lozano Marín");
        mediator.Candidatos.Add(unico);
        var versionAlAbrir = mediator.Version;
        var cut = await AbrirPestanaTrabajadoresAsync(mediator);

        await BotonConTexto(cut, "Añadir trabajador").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => NombresQueEntran(cut).Should().Equal(["Ana García Ruiz", "Noelia Lozano Marín"]));
        mediator.Comandos.Should().ContainSingle().Which.Should().Be(
            new AnadirTrabajadorAVisitaCommand(mediator.VisitaId, unico.Id, versionAlAbrir));
        cut.FindAll(".visitas-candidatos").Should().BeEmpty();
    }

    [Fact]
    public async Task Con_varios_candidatos_Anadir_abre_la_lista_y_no_elige_por_el_usuario()
    {
        var mediator = ConTrabajadores("Ana García Ruiz");
        var (noelia, hector) = (Candidato("Noelia Lozano Marín"), Candidato("Héctor Pastor Rey"));
        mediator.Candidatos.AddRange([noelia, hector]);
        var cut = await AbrirPestanaTrabajadoresAsync(mediator);

        await BotonConTexto(cut, "Añadir trabajador").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.FindAll(".visitas-candidato").Select(c => c.TextContent.Trim())
            .Should().Equal(["Noelia Lozano Marín", "Héctor Pastor Rey"]));
        mediator.Comandos.Should().BeEmpty("con más de un candidato posible no se añade a ninguno hasta que el usuario elige");

        await cut.FindAll(".visitas-candidato").First(c => c.TextContent.Trim() == "Héctor Pastor Rey").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => NombresQueEntran(cut).Should().Equal(["Ana García Ruiz", "Héctor Pastor Rey"]));
        mediator.Comandos.Should().ContainSingle().Which.Should().BeOfType<AnadirTrabajadorAVisitaCommand>()
            .Which.TrabajadorId.Should().Be(hector.Id);
        cut.FindAll(".visitas-candidatos").Should().BeEmpty("tras añadir, la lista se cierra");
    }

    [Fact]
    public async Task El_buscador_de_candidatos_ignora_acentos_y_mayusculas()
    {
        var mediator = ConTrabajadores("Ana García Ruiz");
        mediator.Candidatos.AddRange([Candidato("Noelia Lozano Marín"), Candidato("Héctor Pastor Rey"), Candidato("Sonia Cano Prieto")]);
        var cut = await AbrirPestanaTrabajadoresAsync(mediator);
        await BotonConTexto(cut, "Añadir trabajador").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll(".visitas-candidato").Should().HaveCount(3));

        await cut.Find(".visitas-candidatos input").InputAsync(new ChangeEventArgs { Value = "hector" });

        cut.WaitForAssertion(() => cut.FindAll(".visitas-candidato").Select(c => c.TextContent.Trim())
            .Should().Equal(["Héctor Pastor Rey"]), TimeSpan.FromSeconds(3));

        await cut.Find(".visitas-candidatos input").InputAsync(new ChangeEventArgs { Value = "zzz" });

        cut.WaitForAssertion(() => cut.Find(".visitas-candidatos").TextContent.Should().Contain("Ningún trabajador coincide."), TimeSpan.FromSeconds(3));
        cut.FindAll(".visitas-candidato").Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_candidatos_Anadir_no_manda_nada_ni_abre_una_lista_vacia()
    {
        var mediator = ConTrabajadores("Ana García Ruiz");
        var cut = await AbrirPestanaTrabajadoresAsync(mediator);

        await BotonConTexto(cut, "Añadir trabajador").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => mediator.ConsultasCandidatos.Should().Be(1));
        mediator.Comandos.Should().BeEmpty();
        cut.FindAll(".visitas-candidatos").Should().BeEmpty();
    }

    [Fact]
    public async Task Consulta_ve_quien_entra_sin_que_se_le_ofrezca_quitar_ni_anadir()
    {
        // Los dos comandos son ICommand que AutorizacionEscrituraBehavior deniega a Consulta.
        this.ConRolDeEscritura(Roles.Consulta);
        var mediator = ConTrabajadores("Ana García Ruiz", "Óscar Bravo Nieto");
        mediator.Candidatos.Add(Candidato("Noelia Lozano Marín"));
        var cut = await AbrirPestanaTrabajadoresAsync(mediator);

        NombresQueEntran(cut).Should().HaveCount(2);
        BotonesQuitar(cut).Should().BeEmpty();
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Añadir trabajador");
    }

    [Fact]
    public async Task Una_visita_cancelada_muestra_quien_entraba_sin_acciones()
    {
        var mediator = ConTrabajadores("Ana García Ruiz", "Óscar Bravo Nieto");
        mediator.Cancelada = true;
        mediator.Candidatos.Add(Candidato("Noelia Lozano Marín"));
        var cut = await AbrirPestanaTrabajadoresAsync(mediator);

        NombresQueEntran(cut).Should().HaveCount(2);
        BotonesQuitar(cut).Should().BeEmpty("una Visita cancelada no se modifica; primero se reactiva");
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Añadir trabajador");
    }

    // ── Listado: el recuento de trabajadores ─────────────────────────────

    [Fact]
    public async Task El_recuento_del_listado_abre_quien_entra_y_pulsar_a_uno_lleva_a_la_pestana_Trabajadores()
    {
        var mediator = ConTrabajadores("Ana García Ruiz", "Óscar Bravo Nieto");
        Services.AddScoped<IMediator>(_ => mediator);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        var cut = Render<Visitas>();
        cut.WaitForAssertion(() => cut.Find("tr .ventana-contexto-disparador"));

        var disparador = cut.Find("tr .ventana-contexto-disparador");
        disparador.TextContent.Trim().Should().Be("2");
        disparador.GetAttribute("aria-label").Should().Be("Trabajadores asignados: 2");
        cut.Find("tr .ventana-contexto-pie").TextContent.Trim().Should().Be("Clic en uno para abrir la pestaña Trabajadores",
            "el listado lo ve también quien solo consulta: el pie no promete añadir ni quitar");
        var elementos = cut.FindAll("tr .ventana-contexto-elemento");
        elementos.Select(e => e.QuerySelector(".ventana-contexto-elemento-texto")!.TextContent.Trim())
            .Should().Equal(["Ana García Ruiz", "Óscar Bravo Nieto"]);

        await elementos[1].ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => PestanaActiva(cut).Should().Be("Trabajadores"));
        NombresQueEntran(cut).Should().Equal(["Ana García Ruiz", "Óscar Bravo Nieto"]);
    }
}
