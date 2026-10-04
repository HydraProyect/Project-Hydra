using Bunit;
using CaeManager.Application.Contactos;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacionEmpresa;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacionPorFiltro;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Bandeja.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// «Pedir» un documento que falta o sin confirmar (decisión de Chris del 2026-10-04): el cajón de reclamación en lote enseña, junto
/// a lo que vence, lo que se pide sin vencimiento, y lo envía por el MISMO comando de siempre con sus <c>Pendientes</c>. Aquí se
/// prueba lo que es del cajón: que pida el flag solo cuando se lo dan, que pinte las filas, que cuente y marque como el resto y
/// que mande exactamente lo marcado.
/// </summary>
public class DrawerReclamacionLotePendientesSinFechaTests : BunitContext
{
    private static readonly Guid TrabajadorId = Guid.NewGuid();
    private static readonly Guid ClienteTitularId = Guid.NewGuid();
    private static readonly Guid TipoFaltaId = Guid.NewGuid();
    private static readonly Guid DocumentoSinConfirmarId = Guid.NewGuid();
    private static readonly Guid DocumentoVenceId = Guid.NewGuid();

    private readonly MediatorFalso _mediator = new();

    public DrawerReclamacionLotePendientesSinFechaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddScoped<IMediator>(_ => _mediator);
        Services.AddScoped<ToastService>();
    }

    private static DocumentoPendienteDto Falta() =>
        new(null, TrabajadorId, "Ana Ruiz", TipoFaltaId, "Formación PRL", MotivoPendienteDeReclamacion.Ausente);

    private static DocumentoPendienteDto SinConfirmar() =>
        new(DocumentoSinConfirmarId, TrabajadorId, "Ana Ruiz", Guid.NewGuid(), "Aptitud médica", MotivoPendienteDeReclamacion.SinConfirmar);

    private sealed class MediatorFalso : IMediator
    {
        public List<object> Recibidas { get; } = [];
        public IReadOnlyList<DocumentoReclamableDto> Documentos { get; set; } = [];
        public IReadOnlyList<DocumentoPendienteDto> Pendientes { get; set; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Recibidas.Add(request);
            object respuesta = request switch
            {
                ObtenerTiposDocumentoQuery => (IReadOnlyList<TipoDocumentoListaDto>)[],
                ObtenerTrabajadoresParaSelectorQuery => (IReadOnlyList<TrabajadorSelectorDto>)[new(TrabajadorId, "Ana Ruiz", null, null)],
                ObtenerLoteReclamacionPorFiltroQuery => (IReadOnlyList<LoteReclamacionAgrupadoDto>)
                [
                    new(ClienteTitularId, "Refrielectric S.A.", AmbitoAplicacion.Trabajador, null, Documentos, null,
                        [new DestinatarioAgendaDto(Guid.NewGuid(), "Marta Gil", "marta@example.invalid", ["Formación PRL"])], Pendientes),
                ],
                EnviarReclamacionCommand => Result.Exito(new EnvioReclamacionResultado([], ["marta@example.invalid"], 1)),
                EnviarReclamacionEmpresaCommand => Result.Exito(new EnvioReclamacionResultado([], ["marta@example.invalid"], 1)),
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

    private async Task<IRenderedComponent<DrawerReclamacionLote>> AbrirYContinuarAsync(bool incluirPendientes)
    {
        var cut = Render<DrawerReclamacionLote>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.AmbitosDisponibles, [AmbitoAplicacion.Trabajador])
            .Add(x => x.AmbitoInicial, AmbitoAplicacion.Trabajador)
            .Add(x => x.EntidadIdInicial, TrabajadorId)
            .Add(x => x.IncluirPendientesSinFecha, incluirPendientes));
        cut.WaitForAssertion(() => cut.FindAll(".selector-lote-documental input[type=checkbox]").Should().NotBeEmpty());
        await cut.FindAll(".selector-lote-acciones button").Single(b => b.TextContent.Trim() == "Continuar").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll(".tabla-datos").Should().NotBeEmpty());
        return cut;
    }

    private static IReadOnlyList<DocumentoReclamableDto> UnoQueVence() =>
        [new(DocumentoVenceId, TrabajadorId, "Ana Ruiz", Guid.NewGuid(), "Curso de altura", new DateOnly(2026, 11, 2), EstadoDocumento.Urgente)];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_cajon_pide_lo_pendiente_sin_fecha_solo_cuando_se_lo_dan(bool incluir)
    {
        _mediator.Documentos = UnoQueVence();
        await AbrirYContinuarAsync(incluir);

        _mediator.Recibidas.OfType<ObtenerLoteReclamacionPorFiltroQuery>().Should().ContainSingle()
            .Which.IncluirPendientesSinFecha.Should().Be(incluir);
    }

    [Fact]
    public async Task Pinta_lo_que_falta_y_lo_sin_confirmar_con_su_situacion_y_sin_fecha_de_vencimiento()
    {
        _mediator.Documentos = UnoQueVence();
        _mediator.Pendientes = [Falta(), SinConfirmar()];
        var cut = await AbrirYContinuarAsync(incluirPendientes: true);

        var filas = cut.FindAll(".tabla-datos tbody tr");
        filas.Should().HaveCount(3, "una que vence y dos pedidas sin fecha");
        var falta = filas.Single(f => f.TextContent.Contains("Formación PRL"));
        falta.TextContent.Should().Contain("Falta").And.Contain("Sin fecha");
        filas.Single(f => f.TextContent.Contains("Aptitud médica")).TextContent.Should().Contain("Sin confirmar").And.Contain("Sin fecha");
        cut.FindAll(".tabla-datos input[type=checkbox]").Should().OnlyContain(c => c.HasAttribute("checked"), "como lo que vence, todo viene marcado y el Gestor CAE desmarca");
        cut.FindAll(".tabla-datos input[type=checkbox]")
            .Select(c => c.GetAttribute("aria-label"))
            .Should().Contain("Incluir Formación PRL de Ana Ruiz, que falta, en la reclamación")
            .And.Contain("Incluir Aptitud médica de Ana Ruiz, con la vigencia sin confirmar, en la reclamación");
        cut.Markup.Should().Contain("Enviar (3)");
    }

    [Fact]
    public async Task Envia_exactamente_lo_marcado_por_el_mismo_comando_con_sus_pendientes()
    {
        _mediator.Documentos = UnoQueVence();
        _mediator.Pendientes = [Falta(), SinConfirmar()];
        var cut = await AbrirYContinuarAsync(incluirPendientes: true);

        // Desmarca el «Sin confirmar»: solo viaja lo que falta y lo que vence.
        var casillaSinConfirmar = cut.FindAll(".tabla-datos tbody tr").Single(f => f.TextContent.Contains("Aptitud médica")).QuerySelector("input[type=checkbox]")!;
        await casillaSinConfirmar.ChangeAsync(new ChangeEventArgs { Value = false });
        cut.Markup.Should().Contain("Enviar (2)");

        await cut.FindAll("button").Single(b => b.TextContent.Trim().StartsWith("Enviar (")).ClickAsync(new MouseEventArgs());

        var envio = _mediator.Recibidas.OfType<EnviarReclamacionCommand>().Should().ContainSingle().Which;
        envio.ClienteId.Should().Be(ClienteTitularId);
        envio.DocumentoIds.Should().Equal(DocumentoVenceId);
        envio.Pendientes.Should().Equal(PendienteSinFecha.Ausente(TrabajadorId, TipoFaltaId));
    }

    [Fact]
    public async Task Un_lote_que_solo_tiene_pendientes_se_puede_enviar_y_lleva_el_sin_confirmar_por_su_documento()
    {
        _mediator.Documentos = [];
        _mediator.Pendientes = [SinConfirmar()];
        var cut = await AbrirYContinuarAsync(incluirPendientes: true);

        var enviar = cut.FindAll("button").Single(b => b.TextContent.Trim().StartsWith("Enviar ("));
        enviar.HasAttribute("disabled").Should().BeFalse("hay algo marcado aunque nada venza");
        await enviar.ClickAsync(new MouseEventArgs());

        var envio = _mediator.Recibidas.OfType<EnviarReclamacionCommand>().Should().ContainSingle().Which;
        envio.DocumentoIds.Should().BeEmpty();
        envio.Pendientes.Should().Equal(PendienteSinFecha.SinConfirmar(DocumentoSinConfirmarId));
    }

    [Fact]
    public async Task Sin_nada_marcado_no_se_envia()
    {
        _mediator.Documentos = [];
        _mediator.Pendientes = [Falta()];
        var cut = await AbrirYContinuarAsync(incluirPendientes: true);

        await cut.Find(".tabla-datos input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = false });

        cut.FindAll("button").Single(b => b.TextContent.Trim().StartsWith("Enviar (")).HasAttribute("disabled").Should().BeTrue();
        _mediator.Recibidas.OfType<EnviarReclamacionCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task Desmarcar_un_pendiente_cuenta_como_cambio_sin_guardar()
    {
        _mediator.Documentos = [];
        _mediator.Pendientes = [Falta()];
        var cut = await AbrirYContinuarAsync(incluirPendientes: true);

        await cut.Find(".tabla-datos input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = false });
        await cut.Find(".drawer-panel button.drawer-cerrar").ClickAsync(new MouseEventArgs());

        cut.FindAll("h2").Should().Contain(h => h.TextContent.Trim() == "¿Descartar cambios?");
    }
}
