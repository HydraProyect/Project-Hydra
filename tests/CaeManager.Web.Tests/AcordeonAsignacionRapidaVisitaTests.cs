using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Alertas;
using CaeManager.Application.Asignaciones.Commands.CrearAsignacion;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Asignaciones.Queries.ObtenerDocumentosFaltantesParaAsignacion;
using CaeManager.Application.Asignaciones.Queries.ObtenerTrabajadoresVisitaSinAsignacion;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Centros.Components;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// El toast de "Asignación rápida desde visita" (§ 0.3): la comprobación de
/// documentos faltantes es una lectura aparte, hecha ANTES de crear la
/// asignación, y <c>CrearAsignacionCommand</c> ni la repite ni se detiene por
/// documentos. Igual que el drawer N×M y que Trabajadores.razor, el aviso
/// tiene que hablar de lo que faltaba al comprobarlo — configuración («se
/// pide»), no una obligación legal — y no de una promesa sobre lo que
/// «queda» tras guardar.
/// </summary>
public class AcordeonAsignacionRapidaVisitaTests : BunitContext
{
    public AcordeonAsignacionRapidaVisitaTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly Guid CentroId = Guid.NewGuid();
    private static readonly Guid VisitaId = Guid.NewGuid();
    private static readonly Guid TrabajadorId = Guid.NewGuid();

    private sealed class MediatorFalso : IMediator
    {
        public IReadOnlyList<DocumentoFaltanteDto> Faltantes { get; set; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerAsignacionesDocumentacionPorCentroQuery => (object)Array.Empty<TrabajadorAsignacionDocumentacionDto>(),
                ObtenerTrabajadoresVisitaSinAsignacionQuery => new[] { new TrabajadorSinAsignacionDto(TrabajadorId, "Bea Alonso Ruiz") },
                ObtenerDocumentosFaltantesParaAsignacionQuery => Faltantes,
                CrearAsignacionCommand => Result.Exito(Guid.NewGuid()),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")
            }));

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

    /// <summary>El acordeón monta DrawerGestionDocumento, que los inyecta; el camino de este test no los toca.</summary>
    private sealed class AlmacenArchivosQueNadieDebeTocar : IFileStorageService
    {
        private static Exception NoDeberia() =>
            new NotSupportedException("Este camino no abre archivos; si esto salta, el acordeón cambió de camino.");

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    private sealed class ConversorQueNadieDebeTocar : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Este camino no convierte nada; si esto salta, el acordeón cambió de camino.");
    }

    private IRenderedComponent<AcordeonAsignacionesCentro> Renderizar(MediatorFalso mediador)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
        Services.AddScoped<IConversorWordPdfService, ConversorQueNadieDebeTocar>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        Services.AddScoped<IValidator<CrearAsignacionCommand>>(_ => new InlineValidator<CrearAsignacionCommand>());

        var cut = Render<AcordeonAsignacionesCentro>(p => p
            .Add(a => a.CentroId, CentroId)
            .Add(a => a.CentroNombre, "Planta Zaragoza")
            .Add(a => a.VisitaId, VisitaId)
            .Add(a => a.VisitaFechaFin, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2))));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Bea Alonso Ruiz"));
        return cut;
    }

    private static IElement BotonAsignar(IRenderedComponent<AcordeonAsignacionesCentro> cut) =>
        cut.FindAll(".fila-documento-requerido button").Single(b => b.TextContent.Trim() == "Asignar");

    /// <summary>
    /// El texto habla en pasado de lo que faltaba al comprobarlo — no promete
    /// nada sobre lo que «queda» tras guardar — y de configuración («se
    /// pide»), no de una obligación legal.
    /// </summary>
    [Fact]
    public async Task El_toast_con_faltantes_dice_lo_que_faltaba_al_comprobarlo_sin_hablar_de_obligacion()
    {
        var mediador = new MediatorFalso
        {
            Faltantes = [new DocumentoFaltanteDto(TrabajadorId, "Bea Alonso Ruiz", CentroId, "Planta Zaragoza", Guid.NewGuid(), "Formación PRL específica")]
        };
        var cut = Renderizar(mediador);

        await BotonAsignar(cut).ClickAsync(new MouseEventArgs());

        var toastService = Services.GetRequiredService<ToastService>();
        cut.WaitForAssertion(() => toastService.Mensajes.Should().ContainSingle());
        var mensaje = toastService.Mensajes.Single().Mensaje;
        mensaje.Should().Contain("al comprobarlo le faltaban 1 documento(s) que se piden")
            .And.NotContainEquivalentOf("obligatori", "es configuración (se pide), no una obligación legal")
            .And.NotContain("quedará", "la comprobación es previa: no promete qué habrá tras guardar");
    }

    [Fact]
    public async Task El_toast_sin_faltantes_no_menciona_documentos()
    {
        var mediador = new MediatorFalso { Faltantes = [] };
        var cut = Renderizar(mediador);

        await BotonAsignar(cut).ClickAsync(new MouseEventArgs());

        var toastService = Services.GetRequiredService<ToastService>();
        cut.WaitForAssertion(() => toastService.Mensajes.Should().ContainSingle());
        toastService.Mensajes.Single().Mensaje.Should().Be("Bea Alonso Ruiz asignado a Planta Zaragoza.");
    }
}
