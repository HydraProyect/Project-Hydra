using Bunit;
using CaeManager.Application.Plantillas.Queries.ObtenerDocumentosGenerados;
using CaeManager.Application.Plantillas.Queries.ObtenerPlantillasDocumento;
using CaeManager.Application.Plantillas.Queries.ObtenerTotalDocumentosGeneradosConAvisos;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Plantillas;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// El badge de la pestaña "Generados" es el punto donde se observó el
/// defecto 2026-09-08: <see cref="DocumentosGeneradosPanel"/> notificaba el
/// total YA filtrado de su tabla, y ese número aterrizaba tal cual en el
/// texto de la pestaña. <c>DocumentosGeneradosPanelContadorAvisosTests</c>
/// prueba el panel aislado (qué notifica); este arnés prueba lo que el
/// usuario ve de verdad en la pestaña — un doble que ignorase el conteo
/// real de <see cref="ObtenerTotalDocumentosGeneradosConAvisosQuery"/>
/// pasaría inadvertido en el otro test.
/// </summary>
public class PlantillasTabTests : BunitContext
{
    private sealed class MediatorFalso(int avisosPendientes) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerPlantillasDocumentoQuery => (object)(IReadOnlyList<PlantillaDocumentoListaDto>)[],
                ObtenerTrabajadoresParaSelectorQuery => (IReadOnlyList<TrabajadorSelectorDto>)[],
                ObtenerTotalDocumentosGeneradosConAvisosQuery => avisosPendientes,
                ObtenerDocumentosGeneradosQuery => (IReadOnlyList<DocumentoGeneradoListaDto>)[],
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

    /// <summary>Entra en la pestaña "Generados" — es donde vive <see cref="DocumentosGeneradosPanel"/>, que dispara la consulta del total de avisos.</summary>
    private IRenderedComponent<PlantillasTab> RenderizarEnGenerados(int avisosPendientes)
    {
        Services.AddScoped<IMediator>(_ => new MediatorFalso(avisosPendientes));
        Services.AddScoped<ToastService>();

        var cut = Render<PlantillasTab>();
        cut.FindAll("[role='tab']")[1].Click();
        return cut;
    }

    [Fact]
    public void Con_avisos_pendientes_la_pestana_los_muestra_en_su_etiqueta()
    {
        var cut = RenderizarEnGenerados(avisosPendientes: 3);

        cut.FindAll("[role='tab']")[1].TextContent.Trim().Should().Be("Generados (3 con avisos)");
    }

    [Fact]
    public void Sin_avisos_pendientes_la_pestana_no_pinta_un_cero()
    {
        var cut = RenderizarEnGenerados(avisosPendientes: 0);

        cut.FindAll("[role='tab']")[1].TextContent.Trim().Should().Be("Generados",
            "sin nada que revisar, un «(0 con avisos)» junto al tab lee como alarma vacía");
    }
}
