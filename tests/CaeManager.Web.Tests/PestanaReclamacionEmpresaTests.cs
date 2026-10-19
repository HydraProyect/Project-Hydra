using Bunit;
using CaeManager.Application.Contactos;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacionPorFiltro;
using CaeManager.Application.Reclamaciones.Queries.ObtenerReclamacionAEmpresaEmpleadora;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Empresas.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Empresa 360 · pestaña «Reclamación» (decisión de Chris, 2026-10-19): una tarjeta por lote con sus filas, «Reclamar todo»
/// y «Reclamar» abren <c>DrawerReclamacionLote</c> con esos lotes (el envío es suyo y pide confirmación), y la reclamación
/// por acceso pide a la consulta exactamente el Centro y los Trabajadores que el Gestor CAE ha elegido.
/// </summary>
public class PestanaReclamacionEmpresaTests : BunitContext
{
    private static readonly Guid EmpresaId = Guid.NewGuid();
    private static readonly Guid SevillaId = Guid.NewGuid();
    private static readonly Guid CentroNorteId = Guid.NewGuid();
    private static readonly Guid CentroSurId = Guid.NewGuid();
    private static readonly Guid AnaId = Guid.NewGuid();
    private static readonly Guid BeaId = Guid.NewGuid();
    private static readonly Guid TipoCertificadoId = Guid.NewGuid();
    private static readonly Guid TipoPlanId = Guid.NewGuid();

    private readonly MediatorFalso _mediator = new();

    public PestanaReclamacionEmpresaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddScoped<IMediator>(_ => _mediator);
        Services.AddScoped<ToastService>();
    }

    private static LoteReclamacionAgrupadoDto LoteDeSevilla() => new(
        EmpresaId, "Contratista Reclamada S.L.", AmbitoAplicacion.Empresa, null,
        [
            new(Guid.NewGuid(), AnaId, "Ana Pérez", TipoCertificadoId, "Certificado médico", new DateOnly(2026, 11, 2), EstadoDocumento.Urgente),
            new(Guid.NewGuid(), BeaId, "Bea Ruiz", TipoCertificadoId, "Certificado médico", new DateOnly(2026, 12, 9), EstadoDocumento.Proximo),
        ],
        null,
        [new DestinatarioAgendaDto(Guid.NewGuid(), "Marta Sevilla", "sevilla@contratista.test", ["Certificado médico"])],
        [],
        new DestinoEmpresaEmpleadoraDto(SevillaId, "Sevilla"));

    private static LoteReclamacionAgrupadoDto LoteDeLaAgenda() => new(
        EmpresaId, "Contratista Reclamada S.L.", AmbitoAplicacion.Empresa, null,
        [new(Guid.NewGuid(), null, null, TipoPlanId, "Plan de prevención", new DateOnly(2026, 11, 20), EstadoDocumento.Proximo)],
        null,
        [new DestinatarioAgendaDto(Guid.NewGuid(), "Agenda Contratista", "agenda@contratista.test", ["Plan de prevención"])],
        [new DocumentoPendienteDto(null, null, null, TipoPlanId, "Seguro de responsabilidad civil", MotivoPendienteDeReclamacion.Ausente)],
        new DestinoEmpresaEmpleadoraDto(null, null));

    private static CentroConTrabajadoresDto CentroNorte() => new(
        CentroNorteId, "Centro Norte", [new OpcionFiltroDto(AnaId, "Ana Pérez"), new OpcionFiltroDto(BeaId, "Bea Ruiz")]);

    private static ReclamacionPreventivaEmpresaDto Datos(
        IReadOnlyList<LoteReclamacionAgrupadoDto> lotes, IReadOnlyList<CentroConTrabajadoresDto>? centros = null) =>
        new(lotes, [new OpcionFiltroDto(SevillaId, "Sevilla")], [], centros ?? []);

    private IRenderedComponent<PestanaReclamacionEmpresa> Renderizar(ReclamacionPreventivaEmpresaDto datos)
    {
        _mediator.Preventiva = datos;
        var cut = Render<PestanaReclamacionEmpresa>(p => p.Add(x => x.EmpresaId, EmpresaId));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=reclamacion-preventiva] .tarjeta, [data-testid=reclamacion-preventiva] .estado-vacio").Should().NotBeEmpty());
        return cut;
    }

    [Fact]
    public void Pinta_una_tarjeta_por_lote_con_sus_filas()
    {
        var cut = Renderizar(Datos([LoteDeSevilla(), LoteDeLaAgenda()]));

        var tablas = cut.FindAll("[data-testid=reclamacion-lote]");
        tablas.Should().HaveCount(2);
        cut.Markup.Should().Contain("Delegación Sevilla").And.Contain("Contactos de la empresa");

        var deSevilla = tablas[0].TextContent;
        deSevilla.Should().Contain("Ana Pérez").And.Contain("Bea Ruiz").And.Contain("Certificado médico")
            .And.Contain("Vence el 02/11/2026").And.Contain("Vence el 09/12/2026");

        var deLaAgenda = tablas[1].TextContent;
        deLaAgenda.Should().Contain("Plan de prevención").And.Contain("Vence el 20/11/2026")
            .And.Contain("Seguro de responsabilidad civil").And.Contain("Falta");
        tablas[1].QuerySelectorAll("tbody tr").Select(f => f.QuerySelector("td")!.TextContent.Trim())
            .Should().AllBe("Empresa", "lo de la propia Empresa no cuelga de ningún Trabajador");
    }

    [Fact]
    public async Task Reclamar_todo_abre_el_cajon_con_todos_los_lotes_y_no_envia_nada()
    {
        var cut = Renderizar(Datos([LoteDeSevilla(), LoteDeLaAgenda()]));
        cut.Find("[data-testid=reclamar-todo]").TextContent.Should().Contain("2 correos");

        await cut.Find("[data-testid=reclamar-todo]").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("sevilla@contratista.test").And.Contain("agenda@contratista.test"));
        _mediator.Recibidas.Should().NotContain(r => r.GetType().Name.StartsWith("Enviar"), "abrir el cajón no manda correo");
    }

    [Fact]
    public async Task Reclamar_en_una_tarjeta_abre_el_cajon_solo_con_ese_lote()
    {
        var cut = Renderizar(Datos([LoteDeSevilla(), LoteDeLaAgenda()]));

        var botones = cut.FindAll("button").Where(b => b.TextContent.Trim() == "Reclamar").ToList();
        botones.Should().HaveCount(2);
        await botones[0].ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("sevilla@contratista.test"));
        cut.Markup.Should().NotContain("agenda@contratista.test");
    }

    [Fact]
    public void Sin_nada_que_reclamar_enseña_el_estado_vacio_y_no_ofrece_reclamar()
    {
        var cut = Renderizar(Datos([]));

        cut.Markup.Should().Contain("Nada que reclamar");
        cut.FindAll("[data-testid=reclamar-todo]").Should().BeEmpty();
        cut.FindAll("[data-testid=reclamacion-lote]").Should().BeEmpty();
        cut.FindAll("[data-testid=reclamacion-acceso]").Should().BeEmpty("sin Centros no hay reclamación por acceso que ofrecer");
    }

    [Fact]
    public async Task Elegir_Centro_y_Trabajador_y_preparar_pide_a_la_consulta_exactamente_esos_ids()
    {
        var otroCentro = new CentroConTrabajadoresDto(CentroSurId, "Centro Sur", [new OpcionFiltroDto(AnaId, "Ana Pérez")]);
        _mediator.PorAcceso = [LoteDeSevilla()];
        var cut = Renderizar(Datos([LoteDeSevilla()], [CentroNorte(), otroCentro]));

        cut.FindAll("[data-testid=preparar-acceso]").Should().BeEmpty("hasta elegir un Centro no hay nada que preparar");
        await cut.Find("[data-testid=reclamacion-acceso] select").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = CentroNorteId.ToString() });
        cut.Find("[data-testid=preparar-acceso]").HasAttribute("disabled").Should().BeTrue("sin Trabajadores marcados no se puede preparar");

        await cut.FindAll("[data-testid=reclamacion-acceso] input[type=checkbox]")[0]
            .ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });
        cut.Find("[data-testid=preparar-acceso]").HasAttribute("disabled").Should().BeFalse();
        await cut.Find("[data-testid=preparar-acceso]").ClickAsync(new MouseEventArgs());

        var consulta = _mediator.Recibidas.OfType<ObtenerReclamacionPorAccesoQuery>().Should().ContainSingle().Subject;
        consulta.EmpresaId.Should().Be(EmpresaId);
        consulta.CentroId.Should().Be(CentroNorteId);
        consulta.TrabajadorIds.Should().Equal(AnaId);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("sevilla@contratista.test"));
    }

    [Fact]
    public async Task Si_el_Centro_no_exige_nada_lo_dice_y_no_abre_el_cajon()
    {
        _mediator.PorAcceso = [];
        var cut = Renderizar(Datos([LoteDeSevilla()], [CentroNorte()]));
        await cut.Find("[data-testid=reclamacion-acceso] select").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = CentroNorteId.ToString() });
        await cut.FindAll("[data-testid=reclamacion-acceso] input[type=checkbox]")[1]
            .ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });

        await cut.Find("[data-testid=preparar-acceso]").ClickAsync(new MouseEventArgs());

        cut.Markup.Should().Contain("Ese centro no exige nada vencido ni pendiente");
        cut.Markup.Should().NotContain("sevilla@contratista.test");
        _mediator.Recibidas.OfType<ObtenerReclamacionPorAccesoQuery>().Single().TrabajadorIds.Should().Equal(BeaId);
    }

    private sealed class MediatorFalso : IMediator
    {
        public List<object> Recibidas { get; } = [];
        public ReclamacionPreventivaEmpresaDto Preventiva { get; set; } = new([], [], [], []);
        public IReadOnlyList<LoteReclamacionAgrupadoDto> PorAcceso { get; set; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Recibidas.Add(request);
            object respuesta = request switch
            {
                ObtenerReclamacionPreventivaEmpresaQuery => Preventiva,
                ObtenerReclamacionPorAccesoQuery => PorAcceso,
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            };
            return Task.FromResult((TResponse)respuesta);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }
}
