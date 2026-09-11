using Bunit;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Common;
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
/// «Falta» en el tercer nivel de Centro 360 sale de
/// ResolucionTipoDocumentoCentro.Aplica (ObtenerAsignacionesDocumentacionPorCentroQuery,
/// mismo criterio que Alertas.razor): la fila de este centro si existe y, si
/// no, el valor general del tipo. Atribuirlo solo al centro ("el centro lo
/// exige") mentía en los centros que no han configurado nada — hallazgo real
/// del 2026-09-11 al corregir Alertas.
/// </summary>
public class AcordeonAsignacionesCentroAtribucionFaltaTests : BunitContext
{
    public AcordeonAsignacionesCentroAtribucionFaltaTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediatorFalso : IMediator
    {
        public required IReadOnlyList<TrabajadorAsignacionDocumentacionDto> Trabajadores { get; init; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerAsignacionesDocumentacionPorCentroQuery => Trabajadores,
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
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

    /// <summary>El acordeón monta DrawerGestionDocumento, que los inyecta; cerrado, nadie los toca.</summary>
    private sealed class AlmacenArchivosQueNadieDebeTocar : IFileStorageService
    {
        private static Exception NoDeberia() =>
            new NotSupportedException("Con el drawer cerrado no se abre ningún archivo; si esto salta, el acordeón cambió de camino.");

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    private sealed class ConversorQueNadieDebeTocar : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Con el drawer cerrado no se convierte nada; si esto salta, el acordeón cambió de camino.");
    }

    private void RegistrarServicios(MediatorFalso mediator)
    {
        Services.AddScoped<IMediator>(_ => mediator);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
        Services.AddScoped<IConversorWordPdfService, ConversorQueNadieDebeTocar>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
    }

    [Fact]
    public async Task El_tooltip_de_Falta_atribuye_la_exigencia_a_las_dos_procedencias_posibles()
    {
        var trabajador = new TrabajadorAsignacionDocumentacionDto(
            Guid.NewGuid(), Guid.NewGuid(), "Ruiz Peña, Ana", new DateOnly(2026, 1, 15), EstadoDocumento.Faltante,
            [new DocumentoRequeridoDto(null, Guid.NewGuid(), "Reconocimiento médico", EstadoDocumento.Faltante, null)]);

        RegistrarServicios(new MediatorFalso { Trabajadores = [trabajador] });

        var cut = Render<AcordeonAsignacionesCentro>(p => p
            .Add(a => a.CentroId, Guid.NewGuid())
            .Add(a => a.CentroNombre, "Centro Logístico Norte"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ruiz Peña, Ana"));
        await cut.Find("button.boton-expandir-fila").ClickAsync(new MouseEventArgs());

        // DocumentosVencidos cuenta Faltante como vencido (misma severidad),
        // así que el mismo trabajador pinta OTRA VentanaContexto en el
        // recuento de la fila — acotar al tercer nivel evita leer esa.
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("tabla-documentos-requeridos"));
        var panel = cut.Find(".tabla-documentos-requeridos .ventana-contexto-panel");
        var texto = panel.TextContent;

        texto.Should().Contain("porque este centro lo tiene configurado")
            .And.Contain("si no dice nada, porque el tipo de documento se pide siempre",
                "sin fila del centro, manda el valor general del tipo (ResolucionTipoDocumentoCentro.Aplica)");
        texto.Should().NotContain("Este centro exige el documento",
            "esa frase atribuía la exigencia solo al centro, aunque no hubiera configurado nada");
        texto.Should().NotContainEquivalentOf("obligatori", "es configuración, no una obligación legal");

        var disparador = cut.Find(".tabla-documentos-requeridos .ventana-contexto");
        disparador.GetAttribute("aria-label")!.Should().Contain("porque lo tiene configurado")
            .And.Contain("si no dice nada, porque el tipo se pide siempre");
    }
}
