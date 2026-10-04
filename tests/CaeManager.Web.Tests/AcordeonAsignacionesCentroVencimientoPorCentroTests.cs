using Bunit;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.VolverAPresentarDocumentoEnCentro;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Centros.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Vencimiento por Centro (decisión del propietario del producto, 2026-10-04): cuando un Centro exige un documento con
/// periodicidad propia, la celda de vigencia del tercer nivel de Centro 360 enseña DOS fechas rotuladas («Vence en este
/// Centro» y «Vigencia del documento») y la fila ofrece «Volver a presentar», que registra una presentación nueva en este
/// Centro. Sin periodicidad propia la celda es la de siempre y la acción no aparece.
/// </summary>
public class AcordeonAsignacionesCentroVencimientoPorCentroTests : BunitContext
{
    public AcordeonAsignacionesCentroVencimientoPorCentroTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediatorFalso : IMediator
    {
        public IReadOnlyList<TrabajadorAsignacionDocumentacionDto> Trabajadores { get; init; } = [];
        public Result ResultadoDeVolverAPresentar { get; init; } = Result.Exito();
        public List<VolverAPresentarDocumentoEnCentroCommand> Presentaciones { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            switch (request)
            {
                case ObtenerAsignacionesDocumentacionPorCentroQuery:
                    return Task.FromResult((TResponse)(object)Trabajadores);
                case VolverAPresentarDocumentoEnCentroCommand comando:
                    Presentaciones.Add(comando);
                    return Task.FromResult((TResponse)(object)ResultadoDeVolverAPresentar);
                default:
                    throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.");
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

    private sealed class AlmacenArchivosQueNadieDebeTocar : IFileStorageService
    {
        private static Exception NoDeberia() => new NotSupportedException("Con el drawer cerrado no se abre ningún archivo.");

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    private sealed class ConversorQueNadieDebeTocar : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Con el drawer cerrado no se convierte nada.");
    }

    private static readonly Guid CentroId = Guid.NewGuid();
    private static readonly Guid DocumentoId = Guid.NewGuid();

    private async Task<IRenderedComponent<AcordeonAsignacionesCentro>> RenderizarAsync(MediatorFalso mediator)
    {
        Services.AddLocalization();
        this.ConRolDeEscritura();
        Services.AddScoped<IMediator>(_ => mediator);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
        Services.AddScoped<IConversorWordPdfService, ConversorQueNadieDebeTocar>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        var cut = Render<AcordeonAsignacionesCentro>(p => p
            .Add(a => a.CentroId, CentroId)
            .Add(a => a.CentroNombre, "Centro Logístico Norte"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ruiz Peña, Ana"));
        await cut.Find("button.boton-expandir-fila").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("tabla-documentos-requeridos"));
        return cut;
    }

    private static MediatorFalso ConDocumento(DocumentoRequeridoDto documento) => new()
    {
        Trabajadores =
        [
            new TrabajadorAsignacionDocumentacionDto(
                Guid.NewGuid(), Guid.NewGuid(), "Ruiz Peña, Ana", new DateOnly(2026, 1, 15), documento.Estado, [documento],
                CumplimientoDocumental.Evaluar([documento.Estado]))
        ]
    };

    private static DocumentoRequeridoDto FormacionConPeriodicidad(bool puedeVolverAPresentar = true) => new(
        DocumentoId, Guid.NewGuid(), "Formación art. 19", EstadoDocumento.Vigente, new DateOnly(2030, 10, 1),
        VenceEnElCentro: new DateOnly(2027, 10, 1), PuedeVolverAPresentar: puedeVolverAPresentar);

    [Fact]
    public async Task Con_periodicidad_propia_la_celda_enseña_las_dos_fechas_rotuladas()
    {
        var documento = FormacionConPeriodicidad();
        var cut = await RenderizarAsync(ConDocumento(documento));

        var celda = cut.Find(".tabla-documentos-requeridos [data-vigencia-por-centro]");
        celda.TextContent.Should().Contain("Vence en este Centro: 01/10/2027");
        celda.TextContent.Should().Contain("Vigencia del documento: 01/10/2030",
            "la fecha del propio documento no cambia por presentarlo en un Centro y no puede confundirse con la del Centro");
    }

    [Fact]
    public async Task Sin_periodicidad_propia_la_celda_es_la_de_siempre_y_no_hay_volver_a_presentar()
    {
        var documento = new DocumentoRequeridoDto(
            DocumentoId, Guid.NewGuid(), "Formación art. 19", EstadoDocumento.Vigente, new DateOnly(2030, 10, 1));
        var cut = await RenderizarAsync(ConDocumento(documento));

        cut.FindAll("[data-vigencia-por-centro]").Should().BeEmpty();
        cut.Find(".tabla-documentos-requeridos .celda-documento-vigencia").TextContent.Trim().Should().Be("Caduca 01/10/2030");
        cut.FindAll("[data-volver-a-presentar]").Should().BeEmpty();
    }

    [Fact]
    public async Task Volver_a_presentar_no_se_ofrece_si_la_regla_no_lo_permite()
    {
        var documento = FormacionConPeriodicidad(puedeVolverAPresentar: false);
        var cut = await RenderizarAsync(ConDocumento(documento));

        cut.FindAll("[data-vigencia-por-centro]").Should().HaveCount(1);
        cut.FindAll("[data-volver-a-presentar]").Should().BeEmpty(
            "PuedeVolverAPresentar lo decide ReglaBloqueoDeAcceso; la pantalla no lo recalcula");
    }

    [Fact]
    public async Task Volver_a_presentar_confirma_y_envia_el_documento_y_el_Centro_de_esta_fila()
    {
        var documento = FormacionConPeriodicidad();
        var mediator = ConDocumento(documento);
        var cut = await RenderizarAsync(mediator);

        await cut.Find("[data-volver-a-presentar]").ClickAsync(new MouseEventArgs());
        mediator.Presentaciones.Should().BeEmpty("pide confirmación: una presentación de más aplaza el vencimiento y no se deshace desde la pantalla");

        var confirmar = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Volver a presentar" && b.GetAttribute("data-volver-a-presentar") is null);
        await confirmar.ClickAsync(new MouseEventArgs());

        mediator.Presentaciones.Should().ContainSingle()
            .Which.Should().Be(new VolverAPresentarDocumentoEnCentroCommand(DocumentoId, CentroId));
    }

    [Fact]
    public async Task Si_el_comando_falla_no_se_dice_que_se_presento()
    {
        var documento = FormacionConPeriodicidad();
        var mediator = new MediatorFalso
        {
            Trabajadores = ConDocumento(documento).Trabajadores,
            ResultadoDeVolverAPresentar = Result.Fallo(Error.Crear("Presentacion.NoAplica", "Este documento ya no se puede volver a presentar."))
        };
        var cut = await RenderizarAsync(mediator);

        await cut.Find("[data-volver-a-presentar]").ClickAsync(new MouseEventArgs());
        var confirmar = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Volver a presentar" && b.GetAttribute("data-volver-a-presentar") is null);
        await confirmar.ClickAsync(new MouseEventArgs());

        mediator.Presentaciones.Should().ContainSingle();
        cut.Markup.Should().NotContain("Presentación registrada");
    }
}
