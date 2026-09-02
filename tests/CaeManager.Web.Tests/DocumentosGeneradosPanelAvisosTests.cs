using Bunit;
using CaeManager.Application.Plantillas.Queries.ObtenerDocumentosGenerados;
using CaeManager.Application.Plantillas.Queries.ObtenerPlantillasDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Plantillas;
using CaeManager.Web.Features.Plantillas.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// DEC-5 (propietario, 2026-09-02): un documento generado con campos
/// obligatorios vacíos existe igual, pero tiene que verse distinto de uno
/// limpio — es la superficie donde se consulta el aviso después, cuando el
/// toast de la generación hace rato que desapareció.
///
/// <para>
/// Vigila además la rama por defecto de la tabla, el mismo riesgo que
/// <see cref="RamasPorDefectoDeEstadosUiTests"/> cierra para
/// <c>EstadoDocumento</c>/<c>EstadoCentro</c>: aquí la traducción vive
/// en línea en el <c>.razor</c>, sin clase <c>*Ui</c> que ratchetear, así que
/// el estado nuevo se comprueba renderizando el panel de verdad.
/// </para>
/// </summary>
public class DocumentosGeneradosPanelAvisosTests : BunitContext
{
    /// <summary>El panel lanza tres consultas distintas por el mismo IMediator — responde por tipo, no una única respuesta para todas.</summary>
    private sealed class MediatorPorTipo : IMediator
    {
        public required IReadOnlyList<DocumentoGeneradoListaDto> Generados { get; init; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerDocumentosGeneradosQuery => Generados,
                ObtenerPlantillasDocumentoQuery => (object)Array.Empty<PlantillaDocumentoListaDto>(),
                ObtenerTrabajadoresParaSelectorQuery => Array.Empty<TrabajadorSelectorDto>(),
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

    private static DocumentoGeneradoListaDto Generado(EstadoDocumentoGenerado estado) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Ficha de riesgos",
            Guid.NewGuid(), "Juan Pérez", null, null, DateTime.UtcNow, estado);

    private IRenderedComponent<DocumentosGeneradosPanel> Renderizar(EstadoDocumentoGenerado estado)
    {
        Services.AddScoped<IMediator>(_ => new MediatorPorTipo { Generados = [Generado(estado)] });
        return Render<DocumentosGeneradosPanel>();
    }

    [Fact]
    public void Un_documento_generado_con_avisos_no_se_muestra_igual_que_uno_limpio()
    {
        var cut = Renderizar(EstadoDocumentoGenerado.GeneradoConAvisos);

        var fila = cut.Find("tbody tr");
        fila.TextContent.Should().Contain("Generado con avisos");
        fila.QuerySelector(".badge-advertencia").Should().NotBeNull(
            "el aviso tiene que distinguirse del éxito por algo más que el texto");
        fila.QuerySelector(".badge-exito").Should().BeNull(
            "pintar un documento con obligatorios vacíos como sano es exactamente el fallo que DEC-5 evita");
    }

    [Fact]
    public void Un_documento_generado_limpio_sigue_mostrandose_como_exito()
    {
        var cut = Renderizar(EstadoDocumentoGenerado.Generado);

        var fila = cut.Find("tbody tr");
        fila.QuerySelector(".badge-exito").Should().NotBeNull();
        fila.TextContent.Should().NotContain("avisos");
    }
}
