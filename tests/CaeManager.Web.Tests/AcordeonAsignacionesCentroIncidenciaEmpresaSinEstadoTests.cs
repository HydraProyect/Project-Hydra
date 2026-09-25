using Bunit;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Centros.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Hallazgo de la oleada 2 de Codex sobre la PR #833: una causa de rechazo en
/// plataforma con <c>Ambito.Empresa</c> llega con <c>Estado: null</c> a
/// propósito (CalculoEstadoCentroService — no es un vencimiento de fecha, es
/// un rechazo activo). Antes de este fix, el bloque Empresa de este
/// componente hacía <c>incidencia.Estado!.Value</c> sin comprobar null,
/// bajo el comentario "Estado siempre presente" — cierto antes del fix v2 de
/// ObtenerCentrosQuery.Desglosar, falso después. Este test demuestra que el
/// null real no lanza y que el componente pinta el badge dedicado
/// "Rechazado" en su lugar, en vez de solo probarlo indirectamente a través
/// del DTO de ObtenerCentrosQueryRecuentosTests (integración, no UI).
/// </summary>
public class AcordeonAsignacionesCentroIncidenciaEmpresaSinEstadoTests : BunitContext
{
    public AcordeonAsignacionesCentroIncidenciaEmpresaSinEstadoTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediatorFalso : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerAsignacionesDocumentacionPorCentroQuery =>
                    (IReadOnlyList<TrabajadorAsignacionDocumentacionDto>)[],
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

    [Fact]
    public void La_incidencia_de_Empresa_sin_Estado_no_lanza_y_pinta_el_badge_Rechazado()
    {
        Services.AddScoped<IMediator>(_ => new MediatorFalso());
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
        Services.AddScoped<IConversorWordPdfService, ConversorQueNadieDebeTocar>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        var incidencia = new IncidenciaCentroDto(
            "EPIs — rechazado por la plataforma", AmbitoCausa.Empresa, Estado: null,
            DocumentoId: Guid.NewGuid(), TipoDocumentoId: Guid.NewGuid(), FechaVencimiento: null);

        var cut = Render<AcordeonAsignacionesCentro>(p => p
            .Add(a => a.CentroId, Guid.NewGuid())
            .Add(a => a.CentroNombre, "Centro Logístico Norte")
            .Add(a => a.EmpresaId, Guid.NewGuid())
            .Add(a => a.EmpresaNombre, "Empresa Recuentos S.L.")
            .Add(a => a.IncidenciasEmpresa, [incidencia]));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("tabla-documentos-requeridos"));
        var fila = cut.Find(".tabla-documentos-requeridos .fila-documento-requerido");

        fila.TextContent.Should().Contain("Rechazado",
            "sin Estado, el componente ya no intenta indexar EstadoDocumentoUi con un valor inexistente: pinta un badge propio");
    }
}
